using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Interfaces;
using SqlSugar;

namespace Timer.Backend.Storage;

/// <summary>
/// Authoritative backend submission inbox. This deliberately stays in the SQL storage layer:
/// transport code supplies a validated command but never gains SqlSugar or ModSharp access.
/// </summary>
internal sealed partial class StorageServiceImpl
{
    private const int SubmissionHashVersion = 1;
    private const int MaxSubmissionCheckpoints = TimerConstants.MAX_STAGE - 1;
    private const long MaxSubmissionTimeMicros = 24L * 60 * 60 * 1_000_000;

    private static readonly DateTime MinSubmissionFinishedAtUtc = new(1000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MaxSubmissionFinishedAtUtcExclusive = new(9999, 12, 31, 23, 59, 59, 500, DateTimeKind.Utc);

    internal async Task<TimerBackendRunSubmissionResult> SubmitBackendRunAsync(
        TimerBackendRunSubmissionCommand command, bool acceptNewWrites = true)
    {
        var submission = NormalizeSubmission(command);

        // This inexpensive replay path does not take a map lock. The inbox holds a stable
        // result after commit, so retrying a delivered request cannot append another run.
        var existing = await FindSubmissionAsync(submission.SubmissionIdText);
        if (existing is not null)
        {
            return ToExistingSubmissionResult(existing, submission);
        }

        // Backend writes are authoritative but never provision maps. Doing this preflight
        // outside the record transaction avoids holding a lock just to report a 404; the
        // map is locked/rechecked immediately inside the write transaction below. Map rows are
        // never deleted, so the cached id of an exact name stays valid.
        var mapId = await FindSubmissionMapIdAsync(submission.MapName);
        if (mapId is not { } resolvedMapId)
        {
            if (!acceptNewWrites) throw new TimerBackendSubmissionPolicyException();
            throw new TimerBackendMapNotFoundException(submission.MapName);
        }

        var map = (MapId: resolvedMapId, MapName: submission.MapName);

        if (!acceptNewWrites)
        {
            // An in-flight original write may not yet have committed when the optimistic inbox
            // read above ran. Taking the same map lock and reading again ensures it either
            // commits first (exact replay) or the obsolete policy rejects this new request.
            TimerBackendRunSubmissionResult? replay = null;
            await WithRecordTransactionAsync(async () =>
            {
                replay = null;
                await LockMapAsync(map.MapId);
                var committed = await FindSubmissionAsync(submission.SubmissionIdText);
                if (committed is null) throw new TimerBackendSubmissionPolicyException();
                replay = ToExistingSubmissionResult(committed, submission);
            });
            return replay ?? throw new InvalidOperationException("Policy retry lookup completed without a result.");
        }

        // Existing installations can contain historical runs without the best-run projection.
        // Seed this one board before attempt resolution; the existing per-board cache makes the
        // steady-state path free, while the seed transaction uses the same map lock as writes.
        await EnsureBestRunsSeededAsync(
            map.MapId,
            submission.Kind == TimerBackendRunKind.Main ? RunType.Main : RunType.Stage,
            submission.Style,
            checked((ushort)submission.Track),
            checked((ushort)submission.Stage));

        // A missing player is intentionally an explicit domain failure. Players are never deleted,
        // so checking before the map lock is as good as inside it and keeps the lock short.
        if (!await _db.Queryable<PlayerEntity>().Where(x => x.SteamId == submission.SteamId).AnyAsync(OperationCancellation))
        {
            throw new TimerBackendPlayerNotFoundException(submission.SteamId);
        }

        TimerBackendRunSubmissionResult? result = null;
        var wakeScoreRecalcWorker = false;

        try
        {
            await WithRecordTransactionAsync(async () =>
            {
                // WithRecordTransactionAsync can retry a deadlock. Never carry an accepted
                // result or a wake signal out of a rolled-back attempt.
                result = null;
                wakeScoreRecalcWorker = false;

                // The map lock serializes same-map finishes and record removal, so everything
                // after it is on every same-map writer's critical path: only the run, best-run and
                // inbox writes happen here. A concurrent duplicate of this submission is not
                // re-checked under the lock: the inbox unique key rejects the later insert, the
                // whole attempt rolls back, and the catch below returns the committed receipt.
                await LockMapAsync(map.MapId);

                var run = CreateBackendRunEntity(submission, map.MapId);
                var write = await WriteRunInCurrentRecordTransactionAsync(
                    run,
                    runId => CreateBackendRunSegments(runId, submission, submission.FinishedAtUtc),
                    submission.StyleFactor,
                    enqueueScoreRecalc: submission.Kind == TimerBackendRunKind.Main);

                var (rankState, rank) = write.AttemptResult switch
                {
                    EAttemptResult.NewServerRecord => (TimerBackendRankState.Ready, 1),
                    EAttemptResult.NoNewRecord => (TimerBackendRankState.NotApplicable, 0),
                    _ => (TimerBackendRankState.Pending, 0),
                };

                // Inserted complete, so it needs no second write; its unique key still
                // arbitrates a retry racing this one on another map's lock.
                var inbox = new RunSubmissionEntity
                {
                    SubmissionId = submission.SubmissionIdText,
                    PayloadHash = submission.PayloadHash,
                    HashVersion = SubmissionHashVersion,
                    ContractVersion = TimerBackendRunSubmissionCommand.CurrentContractVersion,
                    RulesetVersion = submission.RulesetVersion,
                    RunId = run.Id,
                    AttemptResult = (int)write.AttemptResult,
                    RankState = (byte)rankState,
                    Rank = rank,
                    FinishedAtUtc = submission.FinishedAtUtc,
                    // Whole seconds: every supported column type (including existing MySQL DATETIME
                    // columns, which round fractions) stores this exactly, so the first receipt
                    // equals what retries and status lookups read back without a query under the lock.
                    ReceivedAtUtc = TruncateToSecond(DateTime.UtcNow),
                };
                inbox.Id = unchecked((ulong)await _db.Insertable(inbox).ExecuteReturnBigIdentityAsync(OperationCancellation));
                result = ToStoredSubmissionResult(
                    inbox, submission.SubmissionId, TimerBackendSubmissionDisposition.Accepted);
                wakeScoreRecalcWorker = write.WakeScoreRecalcWorker;
            });
        }
        catch (Exception ex) when (IsUniqueKeyViolation(ex))
        {
            // Cross-map requests can reach their distinct map locks simultaneously. The inbox
            // unique key is the global arbiter; after its loser rolls back, re-read the durable
            // winner and turn matching retries into the same stable response.
            var raced = await FindSubmissionAsync(submission.SubmissionIdText);
            if (raced is not null)
            {
                return ToExistingSubmissionResult(raced, submission);
            }

            throw;
        }

        if (result is null)
        {
            throw new InvalidOperationException("Backend submission transaction completed without a result.");
        }

        if (wakeScoreRecalcWorker)
        {
            // The outbox row is durable at this point; this only reduces delivery latency.
            WakeScoreRecalcWorker();
        }

        return result;
    }

    internal async Task<TimerBackendRunSubmissionResult?> GetBackendSubmissionStatusAsync(Guid submissionId)
    {
        if (submissionId == Guid.Empty)
        {
            throw new TimerBackendSubmissionValidationException("SubmissionId must not be empty.");
        }

        var row = await FindSubmissionAsync(submissionId.ToString("N"));
        if (row is null)
        {
            return null;
        }

        return ToStoredSubmissionResult(row, submissionId, TimerBackendSubmissionDisposition.AlreadyApplied);
    }

    private async Task<RunSubmissionEntity?> FindSubmissionAsync(string submissionId)
        => await _db.Queryable<RunSubmissionEntity>()
                    .Where(x => x.SubmissionId == submissionId)
                    .FirstAsync(OperationCancellation);

    private static TimerBackendRunSubmissionResult ToExistingSubmissionResult(RunSubmissionEntity row,
                                                                                NormalizedSubmission submission)
    {
        if (row.HashVersion != SubmissionHashVersion
            || !string.Equals(row.PayloadHash, submission.PayloadHash, StringComparison.Ordinal))
        {
            throw new TimerBackendSubmissionConflictException(submission.SubmissionId);
        }

        return ToStoredSubmissionResult(row, submission.SubmissionId, TimerBackendSubmissionDisposition.AlreadyApplied);
    }

    private static TimerBackendRunSubmissionResult ToStoredSubmissionResult(RunSubmissionEntity row,
                                                                              Guid submissionId,
                                                                              TimerBackendSubmissionDisposition disposition)
    {
        if (row.RunId == 0)
        {
            // Rows are written complete, so RunId 0 is a legacy reservation or corrupt row.
            // Never report it as a successful GetSubmissionStatus merely because the key exists.
            throw new InvalidOperationException("Backend submission inbox contains an unfinished reservation.");
        }

        return new TimerBackendRunSubmissionResult
        {
            SubmissionId = submissionId,
            Disposition = disposition,
            RunId = row.RunId,
            AttemptResult = (TimerBackendAttemptResult)row.AttemptResult,
            RankState = (TimerBackendRankState)row.RankState,
            Rank = row.Rank,
            ReceivedAtUtc = DateTime.SpecifyKind(row.ReceivedAtUtc, DateTimeKind.Utc),
        };
    }

    private static DateTime TruncateToSecond(DateTime value)
        => new (value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);

    // Exact-name lookup as before (submission names are not case-folded), served from the map id cache.
    private async Task<ulong?> FindSubmissionMapIdAsync(string mapName)
    {
        if (TryGetCachedMapId(mapName, out var cached))
        {
            return cached;
        }

        if (await FindMapByNameAsync(mapName) is not { } map)
        {
            return null;
        }

        CacheMapId(mapName, map);
        return map.MapId;
    }

    private static RunEntity CreateBackendRunEntity(NormalizedSubmission submission, ulong mapId)
        => new ()
        {
            SteamId = submission.SteamId,
            MapId = mapId,
            RunType = submission.Kind == TimerBackendRunKind.Main ? RunType.Main : RunType.Stage,
            Stage = (ushort)submission.Stage,
            Style = submission.Style,
            Track = (ushort)submission.Track,
            Time = submission.Time,
            Jumps = (uint)submission.Jumps,
            Strafes = (uint)submission.Strafes,
            Sync = submission.Sync,
            VelocityStartX = submission.Motion.VelocityStartX,
            VelocityStartY = submission.Motion.VelocityStartY,
            VelocityStartZ = submission.Motion.VelocityStartZ,
            VelocityEndX = submission.Motion.VelocityEndX,
            VelocityEndY = submission.Motion.VelocityEndY,
            VelocityEndZ = submission.Motion.VelocityEndZ,
            VelocityMaxX = submission.Motion.VelocityMaxX,
            VelocityMaxY = submission.Motion.VelocityMaxY,
            VelocityMaxZ = submission.Motion.VelocityMaxZ,
            VelocityAvgX = submission.Motion.VelocityAvgX,
            VelocityAvgY = submission.Motion.VelocityAvgY,
            VelocityAvgZ = submission.Motion.VelocityAvgZ,
            DateUnixTimeMilliseconds = ToUnixTimeMilliseconds(submission.FinishedAtUtc),
        };

    private static List<RunSegmentEntity> CreateBackendRunSegments(ulong runId,
                                                                     NormalizedSubmission submission,
                                                                     DateTime recordedAtUtc)
    {
        var segments = new List<RunSegmentEntity>(submission.Checkpoints.Count);
        foreach (var checkpoint in submission.Checkpoints)
        {
            segments.Add(new RunSegmentEntity
            {
                RunId = runId,
                Stage = (ushort)checkpoint.CheckpointIndex,
                Time = checkpoint.Time,
                Jumps = (uint)submission.Jumps,
                Strafes = (uint)submission.Strafes,
                Sync = checkpoint.Sync,
                VelocityStartX = checkpoint.Motion.VelocityStartX,
                VelocityStartY = checkpoint.Motion.VelocityStartY,
                VelocityStartZ = checkpoint.Motion.VelocityStartZ,
                VelocityEndX = checkpoint.Motion.VelocityEndX,
                VelocityEndY = checkpoint.Motion.VelocityEndY,
                VelocityEndZ = checkpoint.Motion.VelocityEndZ,
                VelocityMaxX = checkpoint.Motion.VelocityMaxX,
                VelocityMaxY = checkpoint.Motion.VelocityMaxY,
                VelocityMaxZ = checkpoint.Motion.VelocityMaxZ,
                VelocityAvgX = checkpoint.Motion.VelocityAvgX,
                VelocityAvgY = checkpoint.Motion.VelocityAvgY,
                VelocityAvgZ = checkpoint.Motion.VelocityAvgZ,
                DateUnixMilliseconds = ToUnixTimeMilliseconds(recordedAtUtc),
            });
        }

        return segments;
    }

    private static NormalizedSubmission NormalizeSubmission(TimerBackendRunSubmissionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.ContractVersion != TimerBackendRunSubmissionCommand.CurrentContractVersion)
        {
            throw new TimerBackendSubmissionValidationException(
                $"Unsupported contract version {command.ContractVersion}.");
        }

        if (command.SubmissionId == Guid.Empty)
        {
            throw new TimerBackendSubmissionValidationException("SubmissionId must not be empty.");
        }

        if (command.SteamId <= 0)
        {
            throw new TimerBackendSubmissionValidationException("SteamId must be positive.");
        }

        if (command.Kind is not (TimerBackendRunKind.Main or TimerBackendRunKind.Stage))
        {
            throw new TimerBackendSubmissionValidationException("Run kind is invalid.");
        }

        // unchecked: negative values must fail validation, not throw OverflowException in checked builds.
        if (unchecked((uint)command.Style) >= TimerConstants.MAX_STYLE
            || unchecked((uint)command.Track) >= TimerConstants.MAX_TRACK)
        {
            throw new TimerBackendSubmissionValidationException("Style or track is outside the supported range.");
        }

        if (command.Kind == TimerBackendRunKind.Main && command.Stage != 0)
        {
            throw new TimerBackendSubmissionValidationException("Main submissions must have stage zero.");
        }

        if (command.Kind == TimerBackendRunKind.Stage
            && (command.Stage <= 0 || command.Stage >= TimerConstants.MAX_STAGE))
        {
            throw new TimerBackendSubmissionValidationException("Stage submissions must have a stage in the supported range.");
        }

        if (command.TimeMicros <= 0 || command.TimeMicros > MaxSubmissionTimeMicros)
        {
            throw new TimerBackendSubmissionValidationException("TimeMicros must be within the supported 24-hour range.");
        }

        if (command.Jumps < 0 || command.Strafes < 0)
        {
            throw new TimerBackendSubmissionValidationException("Jumps and Strafes must not be negative.");
        }

        if (command.RulesetVersion <= 0)
        {
            throw new TimerBackendSubmissionValidationException("RulesetVersion must be positive.");
        }

        // Inbox and checkpoint timestamps still use SQL DATETIME. Keep one wire/domain
        // range across providers; MySQL can round a fractional final second into year 10000.
        if (command.FinishedAtUtc.Kind != DateTimeKind.Utc
            || command.FinishedAtUtc < MinSubmissionFinishedAtUtc
            || command.FinishedAtUtc >= MaxSubmissionFinishedAtUtcExclusive)
        {
            throw new TimerBackendSubmissionValidationException(
                "FinishedAtUtc must be UTC, at least 1000-01-01 and earlier than 9999-12-31T23:59:59.500Z.");
        }

        if (!double.IsFinite(command.StyleFactor) || command.StyleFactor < 0 || command.StyleFactor > 100)
        {
            throw new TimerBackendSubmissionValidationException("StyleFactor must be a finite trusted value between zero and 100.");
        }

        ValidateSync(command.Sync, "Sync");
        var motion = NormalizeMotion(command.Motion, "Motion");
        var mapName = NormalizeMapName(command.MapName);

        var time = ToSeconds(command.TimeMicros, "TimeMicros");
        var checkpoints = NormalizeCheckpoints(command.Checkpoints, command.TimeMicros);
        var submission = new NormalizedSubmission
        {
            SubmissionId = command.SubmissionId,
            SubmissionIdText = command.SubmissionId.ToString("N"),
            SteamId = command.SteamId,
            MapName = mapName,
            Kind = command.Kind,
            Style = command.Style,
            Track = command.Track,
            Stage = command.Stage,
            TimeMicros = command.TimeMicros,
            Time = time,
            Jumps = command.Jumps,
            Strafes = command.Strafes,
            Sync = command.Sync,
            Motion = motion,
            Checkpoints = checkpoints,
            FinishedAtUtc = command.FinishedAtUtc,
            RulesetVersion = command.RulesetVersion,
            StyleFactor = command.StyleFactor,
        };
        submission.PayloadHash = ComputePayloadHash(submission);
        return submission;
    }

    private static IReadOnlyList<NormalizedCheckpoint> NormalizeCheckpoints(
        IReadOnlyList<TimerBackendSubmissionCheckpoint>? checkpoints,
        long mainTimeMicros)
    {
        if (checkpoints is null)
        {
            throw new TimerBackendSubmissionValidationException("Checkpoints must not be null.");
        }

        if (checkpoints.Count > MaxSubmissionCheckpoints)
        {
            throw new TimerBackendSubmissionValidationException($"At most {MaxSubmissionCheckpoints} checkpoints are allowed.");
        }

        var normalized = new List<NormalizedCheckpoint>(checkpoints.Count);
        var previousIndex = 0;
        var previousTimeMicros = 0L;
        foreach (var checkpoint in checkpoints)
        {
            if (checkpoint is null)
            {
                throw new TimerBackendSubmissionValidationException("Checkpoint must not be null.");
            }

            if (checkpoint.CheckpointIndex <= previousIndex
                || checkpoint.CheckpointIndex >= TimerConstants.MAX_STAGE)
            {
                throw new TimerBackendSubmissionValidationException("Checkpoint indices must be strictly increasing and in range.");
            }

            if (checkpoint.TimeMicros <= previousTimeMicros || checkpoint.TimeMicros > mainTimeMicros)
            {
                throw new TimerBackendSubmissionValidationException("Checkpoint times must be strictly increasing and no later than the run time.");
            }

            ValidateSync(checkpoint.Sync, "Checkpoint.Sync");
            normalized.Add(new NormalizedCheckpoint
            {
                CheckpointIndex = checkpoint.CheckpointIndex,
                TimeMicros = checkpoint.TimeMicros,
                Time = ToSeconds(checkpoint.TimeMicros, "Checkpoint.TimeMicros"),
                Sync = checkpoint.Sync,
                Motion = NormalizeMotion(checkpoint.Motion, "Checkpoint.Motion"),
            });
            previousIndex = checkpoint.CheckpointIndex;
            previousTimeMicros = checkpoint.TimeMicros;
        }

        return normalized;
    }

    private static string NormalizeMapName(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 192 || value != value.Trim())
        {
            throw new TimerBackendSubmissionValidationException("MapName must contain 1 to 192 non-whitespace characters.");
        }

        foreach (var c in value)
        {
            if (!(c is >= 'a' and <= 'z'
                  or >= 'A' and <= 'Z'
                  or >= '0' and <= '9'
                  or '.' or '_' or '-'))
            {
                throw new TimerBackendSubmissionValidationException("MapName contains an unsupported character.");
            }
        }

        return value.ToLowerInvariant();
    }

    private static NormalizedMotion NormalizeMotion(TimerBackendMotion? motion, string fieldName)
    {
        if (motion is null)
        {
            throw new TimerBackendSubmissionValidationException($"{fieldName} must not be null.");
        }

        ValidateFinite(motion.VelocityStartX, $"{fieldName}.VelocityStartX");
        ValidateFinite(motion.VelocityStartY, $"{fieldName}.VelocityStartY");
        ValidateFinite(motion.VelocityStartZ, $"{fieldName}.VelocityStartZ");
        ValidateFinite(motion.VelocityEndX, $"{fieldName}.VelocityEndX");
        ValidateFinite(motion.VelocityEndY, $"{fieldName}.VelocityEndY");
        ValidateFinite(motion.VelocityEndZ, $"{fieldName}.VelocityEndZ");
        ValidateFinite(motion.VelocityMaxX, $"{fieldName}.VelocityMaxX");
        ValidateFinite(motion.VelocityMaxY, $"{fieldName}.VelocityMaxY");
        ValidateFinite(motion.VelocityMaxZ, $"{fieldName}.VelocityMaxZ");
        ValidateFinite(motion.VelocityAvgX, $"{fieldName}.VelocityAvgX");
        ValidateFinite(motion.VelocityAvgY, $"{fieldName}.VelocityAvgY");
        ValidateFinite(motion.VelocityAvgZ, $"{fieldName}.VelocityAvgZ");

        return new NormalizedMotion
        {
            VelocityStartX = motion.VelocityStartX,
            VelocityStartY = motion.VelocityStartY,
            VelocityStartZ = motion.VelocityStartZ,
            VelocityEndX = motion.VelocityEndX,
            VelocityEndY = motion.VelocityEndY,
            VelocityEndZ = motion.VelocityEndZ,
            VelocityMaxX = motion.VelocityMaxX,
            VelocityMaxY = motion.VelocityMaxY,
            VelocityMaxZ = motion.VelocityMaxZ,
            VelocityAvgX = motion.VelocityAvgX,
            VelocityAvgY = motion.VelocityAvgY,
            VelocityAvgZ = motion.VelocityAvgZ,
        };
    }

    private static float ToSeconds(long micros, string fieldName)
    {
        var seconds = micros / 1_000_000d;
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds > float.MaxValue)
        {
            throw new TimerBackendSubmissionValidationException($"{fieldName} cannot be represented as a finite run time.");
        }

        return (float)seconds;
    }

    // MySQL's FLOAT column tops out just below float.MaxValue: MySqlConnector sends float.MaxValue
    // as "3.4028235E+38", which the server rejects as out of range. That surfaced as an Internal
    // error the sender retries forever instead of an InvalidArgument it quarantines. Any float at
    // or below this magnitude is stored by both MySQL and PostgreSQL.
    private const float MaxStoredFloatMagnitude = 3.4e38f;

    private static void ValidateFinite(float value, string fieldName)
    {
        if (!float.IsFinite(value) || Math.Abs(value) > MaxStoredFloatMagnitude)
        {
            throw new TimerBackendSubmissionValidationException($"{fieldName} must be finite and at most {MaxStoredFloatMagnitude} in magnitude.");
        }
    }

    private static void ValidateSync(float value, string fieldName)
    {
        ValidateFinite(value, fieldName);
        if (value < 0 || value > 100)
        {
            throw new TimerBackendSubmissionValidationException($"{fieldName} must be between zero and 100.");
        }
    }

    private static string ComputePayloadHash(NormalizedSubmission submission)
    {
        // A checkpoint contributes exactly 64 bytes. Pre-sizing avoids buffer growth for the
        // common payload and hashing WrittenSpan avoids MemoryStream.ToArray's full copy.
        var payload = new ArrayBufferWriter<byte>(512 + submission.Checkpoints.Count * 64);
        WriteInt32(payload, SubmissionHashVersion);
        WriteInt32(payload, TimerBackendRunSubmissionCommand.CurrentContractVersion);
        WriteUtf8(payload, submission.MapName);
        WriteByte(payload, (byte)submission.Kind);
        WriteInt64(payload, submission.SteamId);
        WriteInt32(payload, submission.Style);
        WriteInt32(payload, submission.Track);
        WriteInt32(payload, submission.Stage);
        WriteInt64(payload, submission.TimeMicros);
        WriteInt32(payload, submission.Jumps);
        WriteInt32(payload, submission.Strafes);
        WriteSingle(payload, submission.Sync);
        WriteMotion(payload, submission.Motion);
        WriteInt32(payload, submission.Checkpoints.Count);
        foreach (var checkpoint in submission.Checkpoints)
        {
            WriteInt32(payload, checkpoint.CheckpointIndex);
            WriteInt64(payload, checkpoint.TimeMicros);
            WriteSingle(payload, checkpoint.Sync);
            WriteMotion(payload, checkpoint.Motion);
        }

        WriteInt64(payload, submission.FinishedAtUtc.Ticks);
        WriteInt32(payload, submission.RulesetVersion);

        Span<byte> digest = stackalloc byte[32];
        var digestLength = SHA256.HashData(payload.WrittenSpan, digest);
        if (digestLength != digest.Length)
        {
            throw new CryptographicException("SHA-256 returned an unexpected digest length.");
        }

        return Convert.ToHexStringLower(digest);
    }

    private static void WriteMotion(ArrayBufferWriter<byte> writer, NormalizedMotion motion)
    {
        WriteSingle(writer, motion.VelocityStartX);
        WriteSingle(writer, motion.VelocityStartY);
        WriteSingle(writer, motion.VelocityStartZ);
        WriteSingle(writer, motion.VelocityEndX);
        WriteSingle(writer, motion.VelocityEndY);
        WriteSingle(writer, motion.VelocityEndZ);
        WriteSingle(writer, motion.VelocityMaxX);
        WriteSingle(writer, motion.VelocityMaxY);
        WriteSingle(writer, motion.VelocityMaxZ);
        WriteSingle(writer, motion.VelocityAvgX);
        WriteSingle(writer, motion.VelocityAvgY);
        WriteSingle(writer, motion.VelocityAvgZ);
    }

    private static void WriteSingle(ArrayBufferWriter<byte> writer, float value)
        => WriteInt32(writer, BitConverter.SingleToInt32Bits(value));

    private static void WriteByte(ArrayBufferWriter<byte> writer, byte value)
    {
        writer.GetSpan(1)[0] = value;
        writer.Advance(1);
    }

    private static void WriteInt32(ArrayBufferWriter<byte> writer, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(writer.GetSpan(sizeof(int)), value);
        writer.Advance(sizeof(int));
    }

    private static void WriteInt64(ArrayBufferWriter<byte> writer, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(writer.GetSpan(sizeof(long)), value);
        writer.Advance(sizeof(long));
    }

    private static void WriteUtf8(ArrayBufferWriter<byte> writer, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        WriteInt32(writer, byteCount);
        var bytesWritten = Encoding.UTF8.GetBytes(value.AsSpan(), writer.GetSpan(byteCount));
        writer.Advance(bytesWritten);
    }

    private sealed class NormalizedSubmission
    {
        public Guid SubmissionId { get; init; }
        public string SubmissionIdText { get; init; } = string.Empty;
        public long SteamId { get; init; }
        public string MapName { get; init; } = string.Empty;
        public TimerBackendRunKind Kind { get; init; }
        public int Style { get; init; }
        public int Track { get; init; }
        public int Stage { get; init; }
        public long TimeMicros { get; init; }
        public float Time { get; init; }
        public int Jumps { get; init; }
        public int Strafes { get; init; }
        public float Sync { get; init; }
        public NormalizedMotion Motion { get; init; } = new ();
        public IReadOnlyList<NormalizedCheckpoint> Checkpoints { get; init; } = [];
        public DateTime FinishedAtUtc { get; init; }
        public int RulesetVersion { get; init; }
        public double StyleFactor { get; init; }
        public string PayloadHash { get; set; } = string.Empty;
    }

    private sealed class NormalizedCheckpoint
    {
        public int CheckpointIndex { get; init; }
        public long TimeMicros { get; init; }
        public float Time { get; init; }
        public float Sync { get; init; }
        public NormalizedMotion Motion { get; init; } = new ();
    }

    private sealed class NormalizedMotion
    {
        public float VelocityStartX { get; init; }
        public float VelocityStartY { get; init; }
        public float VelocityStartZ { get; init; }
        public float VelocityEndX { get; init; }
        public float VelocityEndY { get; init; }
        public float VelocityEndZ { get; init; }
        public float VelocityMaxX { get; init; }
        public float VelocityMaxY { get; init; }
        public float VelocityMaxZ { get; init; }
        public float VelocityAvgX { get; init; }
        public float VelocityAvgY { get; init; }
        public float VelocityAvgZ { get; init; }
    }
}

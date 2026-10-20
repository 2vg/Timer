using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.Backend.Storage.Scheduling;

namespace Timer.Backend.Storage;

internal sealed partial class StorageServiceImpl
{
    private readonly SqlSugarScope               _rootDb;
    private readonly AopEvents                   _sqlHooks;
    private ISqlSugarClient _db => _operationDb.Value ?? (ISqlSugarClient)_rootDb;
    private readonly ILogger<StorageServiceImpl> _logger;
    private readonly ConcurrentDictionary<string, ulong> _mapIdCache = new (StringComparer.Ordinal);
    private readonly AsyncLocal<WorkshopMap?> _workshopMap = new();
    private readonly ConcurrentDictionary<(string mapKey, ulong workshopId), byte> _resolvedWorkshopMaps = new();
    private readonly ConcurrentDictionary<(ulong mapId, RunType runType), byte> _bestRunMapSeededCache = new();
    private readonly ConcurrentDictionary<(ulong mapId, RunType runType), SemaphoreSlim> _bestRunSeedLocks = new();
    private readonly ConcurrentDictionary<(ulong mapId, RunType runType, int style, ushort track, ushort stage), byte> _bestRunSeededCache = new();
    private readonly StorageServiceImpl?          _scoreRecalcWorkerStorage;
    private readonly ScoreRecalcScheduler?        _scoreRecalcScheduler;
    private int _workerStopRequested;
    private int _shutdown;
    private DateTime? _workerStartedAtUtc;

    internal bool HasScoreRecalcWorker => _scoreRecalcScheduler is not null;

    internal SqlSugarScope Db => _rootDb;

    public StorageServiceImpl(DbType                      dbType,
                              string                      connectionString,
                              ILogger<StorageServiceImpl> logger,
                              bool                        enableScoreRecalcWorker = true)
    {
        _logger   = logger;
        _sqlHooks = CreateSqlHooks();
        _rootDb   = CreateClient(dbType, connectionString);

        if (enableScoreRecalcWorker)
        {
            // The outbox worker owns a distinct SqlSugar scope. It must never share a request
            // transaction/connection with the foreground storage instance, and Shutdown waits for
            // its consumer before disposing this scope.
            _scoreRecalcWorkerStorage = new StorageServiceImpl(dbType, connectionString, logger,
                                                                enableScoreRecalcWorker: false);
            _scoreRecalcScheduler = new ScoreRecalcScheduler(
                token => _scoreRecalcWorkerStorage.RunOperationAsync(
                    _scoreRecalcWorkerStorage.ProcessScoreRecalcOutboxBatchAsync, token), logger);
        }
    }

    /// <summary>
    /// Initializes in-memory state and, when requested, applies this storage implementation's
    /// schema bootstrap/migrations. A horizontally scaled HTTP host should set
    /// <paramref name="initializeSchema"/> only on its designated migration instance;
    /// application replicas merely need the already-provisioned schema to serve reads.
    /// </summary>
    public void Init(bool initializeSchema = true, bool startScoreRecalcWorker = true)
    {
        _mapIdCache.Clear();
        _resolvedWorkshopMaps.Clear();
        _bestRunMapSeededCache.Clear();
        _bestRunSeededCache.Clear();

        if (!initializeSchema)
        {
            // Even replicas that skip CodeFirst must not interpret a legacy temporal Date as
            // the new BIGINT mapping or serve writes against an interrupted conversion.
            EnsureRunDateColumnReadyForRuntime();
            EnsurePointsColumnsReadyForRuntime();
            _logger.LogInformation("SQL schema initialization disabled; expecting an already migrated database.");
            if (startScoreRecalcWorker) StartScoreRecalcWorker();
            return;
        }

        RejectUnsupportedNonMasterRunSchema();
        EnsureRunDateColumnReadyForRuntime();
        EnsurePointsColumnsReadyForRuntime(allowBootstrapRecovery: true);
        MigrateReplaySteamIdColumn();

        try
        {
            _db.CodeFirst.InitTables(typeof(MapEntity),
                                     typeof(MapTrackEntity),
                                     typeof(PlayerEntity),
                                     typeof(PlayerMapStatsEntity),
                                     typeof(PlayerBestRunEntity),
                                     typeof(PlayerTrackScoreEntity),
                                     typeof(ScoreRecalcOutboxEntity),
                                     typeof(RunSubmissionEntity),
                                     typeof(RunEntity),
                                     typeof(RunSegmentEntity),
                                     typeof(ReplayEntity),
                                     typeof(ZoneEntity),
                                     typeof(PlayerSettingsEntity),
                                     typeof(StyleFactorEntity));
        }
        catch (Exception e)
        {
            // Propagate: a failed InitTables means the DB is unreachable/broken. Swallowing
            // it would report Init success, register this backend, and displace the working
            // Schema initialization must not report success after a failure.
            _logger.LogError(e, "Error when initializing tables");

            throw;
        }

        // Finish an interrupted first-time initialization, then hold the result to the same
        // strict points contract every later startup checks.
        EnsurePointsTableUniqueIndexes();
        EnsurePointsColumnsReadyForRuntime();

        MigrateLegacyDates();
        RepairInvalidStoredPlayTimes();
        SyncTicks();
        EnsureTrackScoreCoveringIndex();
        EnsureReadIndexes();
        EnsureScoreRecalcOutboxIndexes();
        EnsureRunSubmissionInboxIndex();
        if (startScoreRecalcWorker) StartScoreRecalcWorker();
    }

    /// <summary>
    /// Ensures the covering index that serves the score-recalc delta read
    /// (WHERE MapId=? AND Style=? AND Track=?, no SteamId predicate) exists. SqlSugar's
    /// <c>InitTables</c> creates declared indexes only when it first creates the table, so an existing
    /// production table would otherwise keep full-scanning the scores table on every recalc.
    /// </summary>
    private void EnsureTrackScoreCoveringIndex()
    {
        const string tableName = "surf_player_track_scores";
        const string indexName = "idx_player_track_scores_map_style_track";

        try
        {
            if (!_db.DbMaintenance.IsAnyTable(tableName, false))
            {
                return;
            }

            if (_db.DbMaintenance.IsAnyIndex(indexName))
            {
                return;
            }

            _db.DbMaintenance.CreateIndex(tableName,
                [nameof(PlayerTrackScoreEntity.MapId), nameof(PlayerTrackScoreEntity.Style), nameof(PlayerTrackScoreEntity.Track),
                 nameof(PlayerTrackScoreEntity.SteamId), nameof(PlayerTrackScoreEntity.Points)], indexName, false);
            _logger.LogInformation("Created covering index {Index} on {Table}", indexName, tableName);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to ensure covering index {Index} on {Table}", indexName, tableName);
        }
    }

    // InitTables creates declared indexes only with a new table. Existing databases get the board-order
    // and points indexes here; the board index replaces the older rank index without RunId.
    private void EnsureReadIndexes()
    {
        EnsureIndex("surf_player_best_runs", BestRunsBoardIndex,
                    [nameof(PlayerBestRunEntity.MapId), nameof(PlayerBestRunEntity.RunType), nameof(PlayerBestRunEntity.Style),
                     nameof(PlayerBestRunEntity.Track), nameof(PlayerBestRunEntity.Stage), nameof(PlayerBestRunEntity.BestTime),
                     nameof(PlayerBestRunEntity.RunId), nameof(PlayerBestRunEntity.SteamId)],
                    replaces: "idx_player_best_runs_rank");
        EnsureIndex("surf_players", PlayersPointsIndex, [nameof(PlayerEntity.Points)]);
    }

    internal const string BestRunsBoardIndex = "idx_player_best_runs_board";
    internal const string PlayersPointsIndex = "idx_surf_players_points";

    private void EnsureIndex(string tableName, string indexName, string[] columns, string? replaces = null)
    {
        try
        {
            if (!_db.DbMaintenance.IsAnyTable(tableName, false))
            {
                return;
            }

            if (!_db.DbMaintenance.IsAnyIndex(indexName))
            {
                _db.DbMaintenance.CreateIndex(tableName, columns, indexName, false);
                _logger.LogInformation("Created index {Index} on {Table}", indexName, tableName);
            }

            if (replaces is not null && _db.DbMaintenance.IsAnyIndex(replaces))
            {
                _db.DbMaintenance.DropIndex(replaces, tableName);
                _logger.LogInformation("Dropped index {Index} on {Table}, replaced by {Replacement}", replaces, tableName, indexName);
            }
        }
        catch (Exception e)
        {
            // Read performance only: queries stay correct without it.
            _logger.LogError(e, "Failed to ensure index {Index} on {Table}", indexName, tableName);
        }
    }

    public void Shutdown()
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0) return;
        if (_scoreRecalcScheduler is not null && Volatile.Read(ref _workerStopRequested) == 0)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { StopScoreRecalcWorkerAsync(deadline.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Score worker shutdown exceeded 15 seconds; retaining its SQL scope until the active operation finishes.");
            }
        }
        _scoreRecalcScheduler?.Dispose();
        if (_scoreRecalcWorkerStorage is not null) _ = DisposeWorkerStorageAfterCompletionAsync();
        DisposeIdleClients();
        _rootDb.Dispose();
    }

    internal Task StopScoreRecalcWorkerAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _workerStopRequested, 1);
        return _scoreRecalcScheduler?.StopAsync(cancellationToken) ?? Task.CompletedTask;
    }

    internal void RequestScoreRecalcWorkerStop()
    {
        Interlocked.Exchange(ref _workerStopRequested, 1);
        _scoreRecalcScheduler?.RequestStop();
    }

    private async Task DisposeWorkerStorageAfterCompletionAsync()
    {
        try { await _scoreRecalcScheduler!.Completion; }
        catch (Exception exception) { _logger.LogError(exception, "Score worker stopped unexpectedly."); }
        finally { _scoreRecalcWorkerStorage!.Shutdown(); }
    }

    private void WakeScoreRecalcWorker()
        => _scoreRecalcScheduler?.Wake();

    internal void StartScoreRecalcWorker()
    {
        // Tests and hosts sometimes customize SqlSugar type mapping after constructing this
        // storage. The worker has an independent connection scope, so copy that configuration
        // before its first database operation rather than letting it diverge from the foreground.
        if (_scoreRecalcWorkerStorage is not null)
        {
            _scoreRecalcWorkerStorage.Db.CurrentConnectionConfig.ConfigureExternalServices =
                _db.CurrentConnectionConfig.ConfigureExternalServices;
        }

        if (_scoreRecalcScheduler is not null)
        {
            _workerStartedAtUtc = DateTime.UtcNow;
            _scoreRecalcScheduler.Start();
        }
    }

    public async Task<MapProfile> GetMapInfo(string map)
    {
        var mapKey  = ToMapKey(map);
        var mapInfo = await EnsureMapEntityByKeyAsync(mapKey, map, mayRebind: true);

        var trackTiers = await _db.Queryable<MapTrackEntity>()
                                  .Where(x => x.MapId == mapInfo.MapId)
                                  .ToListAsync(OperationCancellation);

        return ToMapProfile(mapInfo, trackTiers);
    }

    /// <summary>
    /// Reads an existing map profile without the legacy <see cref="GetMapInfo"/> side effect
    /// of creating a map row. HTTP GET endpoints must use this method so that they remain
    /// cacheable and cannot provision data merely by being probed.
    /// </summary>
    internal async Task<MapProfile?> GetExistingMapInfoAsync(string map)
    {
        var mapKey  = ToMapKey(map);
        var mapInfo = await FindMapByNameAsync(mapKey);

        if (mapInfo is null)
        {
            return null;
        }

        _mapIdCache[mapKey] = mapInfo.MapId;

        var trackTiers = await _db.Queryable<MapTrackEntity>()
                                  .Where(x => x.MapId == mapInfo.MapId)
                                  .ToListAsync(OperationCancellation);

        return ToMapProfile(mapInfo, trackTiers);
    }

    public async Task<IReadOnlyList<MapProfile>> GetMapProfilesAsync()
    {
        var maps = await _db.Queryable<MapEntity>()
                            .OrderBy(x => x.File)
                            .ToListAsync(OperationCancellation);

        var tracks = (await _db.Queryable<MapTrackEntity>().ToListAsync(OperationCancellation))
                     .ToLookup(x => x.MapId);

        return maps.Select(x => ToMapProfile(x, tracks[x.MapId].ToList())).ToList();
    }

    /// <summary>
    /// Executes strongly-typed database checks for readiness probes. It intentionally does not
    /// mutate maps, invoke lazy best-run seeding, or use raw ADO commands. A read-only backend
    /// only checks the map table; a write-serving backend additionally verifies the durable
    /// submission Inbox, score-recalculation Outbox, their correctness/performance indexes,
    /// and every table touched by the write transaction before it can accept a write.
    /// </summary>
    internal async Task CheckReadyAsync(bool requireWriteSchema = false,
                                        bool verifyReadRepairAccess = false,
                                        bool forceSchemaVerification = true)
    {
        if (!requireWriteSchema)
        {
            await _db.Queryable<MapEntity>()
                     .Select(x => x.MapId)
                     .Take(1)
                     .ToListAsync(OperationCancellation);
            if (verifyReadRepairAccess) await VerifyReadRepairAccessAsync();
            return;
        }

        await VerifyWriteSchemaMetadataIfDueAsync(forceSchemaVerification);

        // Every probe: typed, cancellable one-row reads of each table the write path uses. They
        // catch a missing table or read permission without the metadata queries above.
        // Select full typed rows even when the tables are empty. This makes a partially-created
        // table fail now instead of waiting for the first live submission to reference a missing
        // column. The result sets remain bounded to one row.
        await ProbeTableAsync<MapEntity>("surf_maps");
        // Every score recalculation joins map-track tiers, including main-track work.
        await ProbeTableAsync<MapTrackEntity>("surf_maps_tracks");
        await ProbeTableAsync<PlayerEntity>("surf_players");
        await ProbeTableAsync<PlayerBestRunEntity>("surf_player_best_runs");
        await ProbeTableAsync<PlayerTrackScoreEntity>("surf_player_track_scores");
        await ProbeTableAsync<RunEntity>("surf_runs");
        await ProbeTableAsync<RunSegmentEntity>("surf_runs_segments");
        await ProbeTableAsync<RunSubmissionEntity>("surf_run_submissions");
        await ProbeTableAsync<ScoreRecalcOutboxEntity>("surf_score_recalc_outbox");

        if (verifyReadRepairAccess) await VerifyReadRepairAccessAsync();
    }

    /// <summary>A trivial read used to tell a database outage apart from other failures.</summary>
    internal async Task<bool> PingAsync()
    {
        await _db.Queryable<MapEntity>().Select(x => x.MapId).Take(1).ToListAsync(OperationCancellation);
        return true;
    }

    private async Task ProbeTableAsync<T>(string tableName) where T : class, new()
    {
        try
        {
            await _db.Queryable<T>().Take(1).ToListAsync(OperationCancellation);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Keep the stable operator-facing error the metadata check used to give.
            throw new InvalidOperationException(
                $"Timer backend write schema is not ready; table {tableName} is missing or not readable.", exception);
        }
    }

    private static readonly TimeSpan SchemaReverificationInterval = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _schemaVerificationGate = new(1, 1);
    private long _schemaVerifiedAtTicks;
    private volatile bool _schemaVerificationFailed;

    /// <summary>
    /// SqlSugar's table/index metadata calls are synchronous and cannot be cancelled, so a stalled
    /// database would pin one thread per readiness probe. Run them at startup (forced) and then at
    /// most once per <see cref="SchemaReverificationInterval"/>, by one caller at a time; concurrent
    /// probes rely on the most recent result.
    /// </summary>
    private async Task VerifyWriteSchemaMetadataIfDueAsync(bool force)
    {
        var verifiedAt = Interlocked.Read(ref _schemaVerifiedAtTicks);
        if (!force && !_schemaVerificationFailed && verifiedAt != 0
            && DateTime.UtcNow.Ticks - verifiedAt < SchemaReverificationInterval.Ticks)
        {
            return;
        }

        if (!await _schemaVerificationGate.WaitAsync(force ? Timeout.Infinite : 0, OperationCancellation))
        {
            if (verifiedAt != 0 && !_schemaVerificationFailed) return;
            throw new InvalidOperationException("Timer backend write schema verification is in progress or failed.");
        }

        try
        {
            VerifyWriteSchemaMetadata();
            await ValidateWriteIndexesAsync();
            _schemaVerificationFailed = false;
            Interlocked.Exchange(ref _schemaVerifiedAtTicks, DateTime.UtcNow.Ticks);
        }
        catch
        {
            _schemaVerificationFailed = true;
            throw;
        }
        finally
        {
            _schemaVerificationGate.Release();
        }
    }

    private long _readRepairInsertVerifiedAtTicks;

    /// <summary>
    /// Read repair inserts/updates surf_player_best_runs under a surf_maps row lock. UPDATE and
    /// FOR UPDATE privileges are checked before any row is matched, so those statements change
    /// nothing yet fail for a SELECT-only principal; they run on every probe. INSERT privilege can
    /// only be proven by a real insert, which is always rolled back and runs at most once per
    /// <see cref="SchemaReverificationInterval"/> (a rolled-back insert still consumes an identity).
    /// </summary>
    private async Task VerifyReadRepairAccessAsync()
    {
        await VerifyReadRepairUpdateAccessAsync();

        var verifiedAt = Interlocked.Read(ref _readRepairInsertVerifiedAtTicks);
        if (verifiedAt != 0 && DateTime.UtcNow.Ticks - verifiedAt < SchemaReverificationInterval.Ticks)
        {
            return;
        }

        try
        {
            await WithRecordTransactionAsync(async () =>
            {
                // SteamId -1 / MapId 0 / style -1 never identify a real board, so this cannot
                // collide with (or briefly shadow) a player's best run before the rollback.
                await _db.Insertable(new PlayerBestRunEntity
                         {
                             SteamId = -1, MapId = 0, RunType = RunType.Main, Style = -1, Track = 0, Stage = 0,
                             RunId = 0, BestTime = 0, UpdatedAtUnixMilliseconds = ToUnixTimeMilliseconds(DateTime.UtcNow),
                         })
                         .ExecuteCommandAsync(OperationCancellation);
                throw new ReadinessProbeRollbackException();
            });
        }
        catch (ReadinessProbeRollbackException)
        {
            // Expected: the insert succeeded and was rolled back.
        }

        Interlocked.Exchange(ref _readRepairInsertVerifiedAtTicks, DateTime.UtcNow.Ticks);
    }

    private sealed class ReadinessProbeRollbackException : Exception;

    private async Task VerifyReadRepairUpdateAccessAsync()
    {
        await WithRecordTransactionAsync(async () =>
        {
            await _db.Updateable<PlayerBestRunEntity>()
                     .SetColumns(x => x.UpdatedAtUnixMilliseconds == x.UpdatedAtUnixMilliseconds)
                     .Where(x => x.Id == 0)
                     .ExecuteCommandAsync(OperationCancellation);
            if (_db.CurrentConnectionConfig.DbType != DbType.Sqlite)
            {
                await _db.Queryable<MapEntity>()
                         .Where(x => x.MapId == 0)
                         .TranLock(DbLockType.Wait)
                         .Select(x => x.MapId)
                         .ToListAsync(OperationCancellation);
            }
        });
    }

    private void VerifyWriteSchemaMetadata()
    {
        const string mapTableName = "surf_maps";
        const string submissionTableName = "surf_run_submissions";
        const string outboxTableName = "surf_score_recalc_outbox";
        const string submissionIndexName = "idx_surf_run_submissions_submission_unique";
        const string outboxUniqueIndexName = "idx_score_recalc_outbox_unique";
        const string outboxPendingIndexName = "idx_score_recalc_outbox_pending";
        const string trackScoreCoveringIndexName = "idx_player_track_scores_map_style_track";

        var missing = new List<string>();

        foreach (var tableName in new[]
                 {
                     mapTableName,
                     "surf_maps_tracks",
                     "surf_players",
                     "surf_player_best_runs",
                     "surf_player_track_scores",
                     "surf_runs",
                     "surf_runs_segments",
                     submissionTableName,
                     outboxTableName,
                 })
        {
            if (!_db.DbMaintenance.IsAnyTable(tableName, false))
            {
                missing.Add($"table {tableName}");
            }
        }

        // The Inbox index is the correctness boundary for concurrent/retried submissions.
        // Check it only after confirming its table exists so a missing table produces the
        // more actionable table error rather than a provider-specific metadata failure.
        if (_db.DbMaintenance.IsAnyTable(submissionTableName, false)
            && !_db.DbMaintenance.IsAnyIndex(submissionIndexName))
        {
            missing.Add($"unique index {submissionIndexName} on {submissionTableName}");
        }

        if (_db.DbMaintenance.IsAnyTable(outboxTableName, false))
        {
            if (!_db.DbMaintenance.IsAnyIndex(outboxUniqueIndexName))
            {
                missing.Add($"unique index {outboxUniqueIndexName} on {outboxTableName}");
            }

            if (!_db.DbMaintenance.IsAnyIndex(outboxPendingIndexName))
            {
                missing.Add($"index {outboxPendingIndexName} on {outboxTableName}");
            }
        }

        if (_db.DbMaintenance.IsAnyTable("surf_player_track_scores", false)
            && !_db.DbMaintenance.IsAnyIndex(trackScoreCoveringIndexName))
        {
            missing.Add($"index {trackScoreCoveringIndexName} on surf_player_track_scores");
        }

        if (missing.Count != 0)
        {
            throw new InvalidOperationException(
                $"Timer backend write schema is not ready; missing {string.Join(", ", missing)}.");
        }
    }

    public async Task UpdateMapInfo(MapProfile info)
    {
        var mapKey = ToMapKey(info.MapName);

        var mapId = await EnsureMapIdByNameAsync(info.MapName);
        var tier = GetTier(info.Tier, 0);
        var stages = ToUInt16(info.Stages);
        await WithRecordTransactionAsync(async () =>
        {
            await LockMapAsync(mapId);
            // Map metadata only. PlayCount/TotalPlayTime are session counters and must
            // never be copied from an old per-server map-profile snapshot.
            // Do not copy a stale BasePot over an administrator's concurrent change.
            await _db.Updateable<MapEntity>()
                .SetColumns(x => x.File == mapKey)
                .SetColumns(x => x.Tier == tier)
                .SetColumns(x => x.Stages == stages)
                .SetColumns(x => x.Bonuses == info.Bonuses)
                .Where(x => x.MapId == mapId).ExecuteCommandAsync(OperationCancellation);
            await SyncMapTrackTiersAsync(mapId, info.Tier);
        });

        // Cache writes only after a successful commit, so a rollback can never leave
        // the caches holding state from a transaction that never happened.
        _mapIdCache[mapKey] = mapId;
    }

    public async Task IncrementMapStatsAsync(string mapName, float deltaSeconds)
    {
        ValidatePlayTimeDelta(deltaSeconds);
        var maximumCurrentTime = (double)MaximumStoredPlayTimeSeconds - deltaSeconds;
        var mapId = await EnsureMapIdByNameAsync(mapName);
        var now   = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await WithRecordTransactionAsync(async () =>
        {
            await LockMapAsync(mapId);
            var updated = await _db.Updateable<MapEntity>()
                                   .SetColumns(x => x.PlayCount == x.PlayCount + 1)
                                   .SetColumns(x => x.TotalPlayTime == x.TotalPlayTime + deltaSeconds)
                                   .SetColumns(x => x.LastPlayedAtUnixMilliseconds == now)
                                   .Where(x => x.MapId == mapId && x.PlayCount >= 0 && x.PlayCount < int.MaxValue
                                               && x.TotalPlayTime >= 0 && x.TotalPlayTime <= maximumCurrentTime)
                                   .ExecuteCommandAsync(OperationCancellation);
            if (updated != 1)
            {
                throw new InvalidOperationException("Map session counters could not be incremented.");
            }
        });
    }

}

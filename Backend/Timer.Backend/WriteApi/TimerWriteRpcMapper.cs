using System;
using System.Collections.Generic;
using Grpc.Core;
using Source2Surf.Timer.Backend.Rpc.Contracts;
using Source2Surf.Timer.Shared;
using Timer.Backend.Configuration;
using Timer.Backend.Storage;

namespace Timer.Backend.WriteApi;

/// <summary>
/// Explicit wire-to-domain conversion. Numeric conversion and timestamp bounds are checked
/// before reaching persistence so malformed MessagePack requests yield InvalidArgument rather
/// than an implementation-dependent Unknown status.
/// </summary>
internal static class TimerWriteRpcMapper
{
    public static TimerBackendPlayerProfileCommand ToPlayerProfileCommand(EnsurePlayerProfileRequest request)
    {
        if (request is null || request.SteamId <= 0 || !IsValidPlayerName(request.Name))
        {
            throw TimerWriteRpcErrors.InvalidArgument();
        }

        return new TimerBackendPlayerProfileCommand
        {
            SteamId = request.SteamId,
            Name = request.Name,
        };
    }

    public static EnsurePlayerProfileResponse ToPlayerProfileResponse(TimerBackendPlayerProfileResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        try
        {
            return new EnsurePlayerProfileResponse
            {
                PlayerId = result.PlayerId,
                SteamId = result.SteamId,
                Name = result.Name,
                Points = result.Points,
                JoinDateUnixTimeMilliseconds = ToUnixTimeMilliseconds(result.JoinDateUtc),
                LastSeenDateUnixTimeMilliseconds = ToUnixTimeMilliseconds(result.LastSeenDateUtc),
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            throw TimerWriteRpcErrors.InternalMappingFailure();
        }
    }

    public static TimerBackendRunSubmissionCommand ToCommand(SubmitRunRequest request,
                                                              TimerWriteApiOptions options)
        => ToCommandCore(request, options, replayAware: false).Command;

    /// <summary>
    /// Keep the original submitted ruleset in the canonical payload when the backend policy
    /// has changed. The storage inbox can then acknowledge an exact retry, but a policy-mismatched
    /// submission must never be accepted as a new write.
    /// </summary>
    public static (TimerBackendRunSubmissionCommand Command, bool AcceptNewWrites) ToReplayAwareCommand(
        SubmitRunRequest request, TimerWriteApiOptions options)
        => ToCommandCore(request, options, replayAware: true);

    private static (TimerBackendRunSubmissionCommand Command, bool AcceptNewWrites) ToCommandCore(
        SubmitRunRequest request, TimerWriteApiOptions options, bool replayAware)
    {
        if (request is null)
        {
            throw TimerWriteRpcErrors.InvalidArgument();
        }
        ArgumentNullException.ThrowIfNull(options);

        if (!replayAware && request.RulesetVersion != options.RulesetVersion)
        {
            throw TimerWriteRpcErrors.RulesetMismatch();
        }

        // unchecked: a negative style must be rejected, not throw OverflowException in checked builds.
        if (unchecked((uint)request.Style) >= TimerConstants.MAX_STYLE)
        {
            throw TimerWriteRpcErrors.InvalidArgument();
        }

        var styleEnabled = options.StyleFactors.TryGetValue(request.Style, out var styleFactor);
        if (!replayAware && !styleEnabled)
        {
            // A missing factor means that style is not enabled for remote submissions.
            throw TimerWriteRpcErrors.StyleDisabled();
        }

        if (request.Checkpoints is null || request.Motion is null)
        {
            throw TimerWriteRpcErrors.InvalidArgument();
        }

        if (request.Checkpoints.Length > TimerConstants.MAX_STAGE - 1)
        {
            throw TimerWriteRpcErrors.InvalidArgument();
        }

        try
        {
            var checkpoints = new TimerBackendSubmissionCheckpoint[request.Checkpoints.Length];
            for (var index = 0; index < checkpoints.Length; index++)
            {
                var checkpoint = request.Checkpoints[index]
                                 ?? throw TimerWriteRpcErrors.InvalidArgument();
                if (checkpoint.Motion is null)
                {
                    throw TimerWriteRpcErrors.InvalidArgument();
                }

                checkpoints[index] = new TimerBackendSubmissionCheckpoint
                {
                    CheckpointIndex = checked((int)checkpoint.Index),
                    TimeMicros = checkpoint.TimeMicros,
                    Sync = checkpoint.Sync,
                    Motion = ToMotion(checkpoint.Motion),
                };
            }

            var command = new TimerBackendRunSubmissionCommand
            {
                ContractVersion = request.ContractVersion,
                SubmissionId = request.SubmissionId,
                SteamId = request.SteamId,
                MapName = request.MapName ?? throw TimerWriteRpcErrors.InvalidArgument(),
                Kind = ToRunKind(request.RunKind),
                Style = request.Style,
                Track = request.Track,
                Stage = request.Stage,
                TimeMicros = request.TimeMicros,
                Jumps = request.Jumps,
                Strafes = request.Strafes,
                Sync = request.Sync,
                Motion = ToMotion(request.Motion),
                Checkpoints = checkpoints,
                FinishedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(request.FinishedAtUnixTimeMilliseconds).UtcDateTime,
                // The requested version is part of the immutable payload hash. New writes
                // are gated by AcceptNewWrites; old exact retries retain their original hash.
                RulesetVersion = request.RulesetVersion,
                StyleFactor = styleEnabled ? styleFactor : 1,
            };
            return (command, request.RulesetVersion == options.RulesetVersion && styleEnabled);
        }
        catch (OverflowException)
        {
            throw TimerWriteRpcErrors.InvalidArgument();
        }
        catch (ArgumentOutOfRangeException)
        {
            throw TimerWriteRpcErrors.InvalidArgument();
        }
    }

    public static SubmitRunResponse ToResponse(TimerBackendRunSubmissionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        try
        {
            return new SubmitRunResponse
            {
                SubmissionId = result.SubmissionId,
                RunId = result.RunId,
                AttemptResult = result.AttemptResult switch
                {
                    TimerBackendAttemptResult.NoNewRecord => AttemptResult.NoNewRecord,
                    TimerBackendAttemptResult.NewPersonalRecord => AttemptResult.NewPersonalRecord,
                    TimerBackendAttemptResult.NewServerRecord => AttemptResult.NewServerRecord,
                    _ => throw new InvalidOperationException("Unknown backend attempt result."),
                },
                Rank = result.Rank,
                ReceivedAtUnixTimeMilliseconds = new DateTimeOffset(
                    DateTime.SpecifyKind(result.ReceivedAtUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
                Disposition = result.Disposition switch
                {
                    TimerBackendSubmissionDisposition.Accepted => SubmissionDisposition.Accepted,
                    TimerBackendSubmissionDisposition.AlreadyApplied => SubmissionDisposition.AlreadyApplied,
                    _ => throw new InvalidOperationException("Unknown backend submission disposition."),
                },
                RankState = result.RankState switch
                {
                    TimerBackendRankState.NotApplicable => RankState.NotApplicable,
                    TimerBackendRankState.Pending => RankState.Pending,
                    TimerBackendRankState.Ready => RankState.Ready,
                    _ => throw new InvalidOperationException("Unknown backend rank state."),
                },
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            throw TimerWriteRpcErrors.InternalMappingFailure();
        }
    }

    private static TimerBackendRunKind ToRunKind(RunKind kind)
        => kind switch
        {
            RunKind.Main => TimerBackendRunKind.Main,
            RunKind.Stage => TimerBackendRunKind.Stage,
            _ => throw TimerWriteRpcErrors.InvalidArgument(),
        };

    private static bool IsValidPlayerName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 192 || name != name.Trim())
        {
            return false;
        }

        foreach (var character in name)
        {
            if (char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }

    private static long ToUnixTimeMilliseconds(DateTime value)
        => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static TimerBackendMotion ToMotion(MotionDto motion)
        => new ()
        {
            VelocityStartX = motion.StartX,
            VelocityStartY = motion.StartY,
            VelocityStartZ = motion.StartZ,
            VelocityAvgX = motion.AverageX,
            VelocityAvgY = motion.AverageY,
            VelocityAvgZ = motion.AverageZ,
            VelocityMaxX = motion.MaxX,
            VelocityMaxY = motion.MaxY,
            VelocityMaxZ = motion.MaxZ,
            VelocityEndX = motion.EndX,
            VelocityEndY = motion.EndY,
            VelocityEndZ = motion.EndZ,
        };
}

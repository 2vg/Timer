using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using MagicOnion;
using MagicOnion.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Backend.Rpc.Contracts;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Interfaces;
using Timer.Backend.Configuration;
using Timer.Backend.Endpoints;
using Timer.Backend.Storage;

namespace Timer.Backend.WriteApi;

/// <summary>
/// The game server's reads and its writes other than run submissions. Served next to
/// <see cref="TimerWriteServiceV1"/> on the same listeners.
/// </summary>
public sealed class TimerStorageServiceV1 : ServiceBase<ITimerStorageServiceV1>, ITimerStorageServiceV1
{
    // The game server's encoding of every setting stays far below this.
    private const int MaxPlayerSettingsBytes = 256;

    private const int    MaxTopPlayers  = 50;
    private const double MaxStyleFactor = 100;
    private const int MaxRankedLookups = 64; // a full server
    private const int MaxStoredReplayLookups = 1000; // the plugin's chunk size
    private const int MaxReplayUrlLength = 1024;

    private readonly TimerBackendStorage            _owner;
    private readonly TimerBackendGameStorage        _storage;
    private readonly TimerWriteApiOptions           _options;
    private readonly ILogger<TimerStorageServiceV1> _logger;
    private readonly TimerBackendRuntimeOptions     _runtimeOptions;

    public TimerStorageServiceV1(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _owner          = services.GetRequiredService<TimerBackendStorage>();
        _storage        = services.GetRequiredService<TimerBackendGameStorage>();
        _options        = services.GetRequiredService<TimerWriteApiOptions>();
        _logger         = services.GetRequiredService<ILogger<TimerStorageServiceV1>>();
        _runtimeOptions = services.GetService<TimerBackendRuntimeOptions>() ?? new TimerBackendRuntimeOptions();
    }

    public async UnaryResult<MapInfoDto> GetMapInfoAsync(string mapName, ulong workshopId)
        => await RunAsync(async token => TimerStorageRpcMapper.ToDto(
                              await _storage.GetMapInfoAsync(Map(mapName), workshopId, token)));

    public async UnaryResult<MapInfoDto[]> GetMapProfilesAsync()
        => await RunAsync(async token => TimerStorageRpcMapper.ToDto(await _storage.GetMapProfilesAsync(token)));

    public async UnaryResult<string[]> GetAllMapNamesAsync()
        => await RunAsync(async token =>
        {
            var names  = await _owner.GetAllMapNamesAsync(token);
            var result = new string[names.Count];

            for (var i = 0; i < result.Length; i++)
            {
                result[i] = names[i];
            }

            return result;
        });

    public async UnaryResult IncrementMapStatsAsync(string mapName, ulong workshopId, float deltaSeconds)
        => await RunAsync(token => Done(_storage.IncrementMapStatsAsync(Map(mapName), workshopId, Delta(deltaSeconds), token)));

    public async UnaryResult<ScoreJobsDto> SetMapTierAsync(string mapName, ulong workshopId, byte tier)
        => await RunAsync(async token =>
        {
            if (tier == 0)
            {
                throw TimerWriteRpcErrors.InvalidArgument();
            }

            return TimerStorageRpcMapper.ToDto(
                await _storage.SetMapTierAsync(Map(mapName), workshopId, tier, ScorePolicy(), token));
        });

    public async UnaryResult<ScoreJobsDto> SetMapRankedAsync(string mapName, ulong workshopId, bool ranked)
        => await RunAsync(async token => TimerStorageRpcMapper.ToDto(
                              await _storage.SetMapRankedAsync(Map(mapName), workshopId, ranked, ScorePolicy(), token)));

    public async UnaryResult<ScoreJobsDto> RecalculateScoresAsync(string? mapName, ulong workshopId)
        => await RunAsync(async token => TimerStorageRpcMapper.ToDto(
                              await _storage.RecalculateScoresAsync(mapName is null ? null : Map(mapName),
                                                                    workshopId, ScorePolicy(), token)));

    public async UnaryResult<RecordDto[]> GetMapRecordsAsync(string  mapName,
                                                             ulong   workshopId,
                                                             RunKind kind,
                                                             bool    allBoards,
                                                             int     style,
                                                             int     track,
                                                             int     stage,
                                                             int     limit)
        => await RunAsync(async token => TimerStorageRpcMapper.ToDto(
                              await _storage.GetMapRecordsAsync(Map(mapName), workshopId, Stage(kind), allBoards,
                                                                Style(style), Track(track), StageNumber(stage),
                                                                Limit(limit), token)));

    public async UnaryResult<RecordDto[]> GetPlayerRecordsAsync(ulong steamId, string mapName, ulong workshopId, RunKind kind)
        => await RunAsync(async token => TimerStorageRpcMapper.ToDto(
                              await _storage.GetPlayerRecordsAsync(steamId, Map(mapName), workshopId, Stage(kind), token)));

    public async UnaryResult<RecordDto[]> GetRecentRecordsAsync(string mapName, ulong workshopId, ulong steamId, int limit)
        => await RunAsync(async token => TimerStorageRpcMapper.ToDto(
                              await _storage.GetRecentRecordsAsync(Map(mapName), workshopId, steamId, Limit(limit), token)));

    public async UnaryResult<RecordDto[]> GetPlayerRunsAsync(string mapName,
                                                             ulong  workshopId,
                                                             ulong  steamId,
                                                             int    style,
                                                             int    track,
                                                             int    stage,
                                                             int    limit)
        => await RunAsync(async token => TimerStorageRpcMapper.ToDto(
                              await _storage.GetPlayerRunsAsync(Map(mapName), workshopId, steamId, Style(style),
                                                                Track(track), StageNumber(stage), Limit(limit), token)));

    public async UnaryResult<RecordCheckpointDto[]> GetRecordCheckpointsAsync(long recordId)
        => await RunAsync(async token => TimerStorageRpcMapper.ToDto(await _owner.GetRecordCheckpointsAsync(recordId, token)));

    public async UnaryResult RemoveMapRecordsAsync(string mapName, ulong workshopId)
        => await RunAsync(token => Done(_storage.RemoveMapRecordsAsync(Map(mapName), workshopId, token)));

    public async UnaryResult<DeletedRunDto?> DeleteRunAsync(string mapName, ulong workshopId, ulong runId)
        => await RunAsync(async token => await _storage.DeleteRunAsync(Map(mapName), workshopId, runId, _options.StyleFactors, token)
                                             is { } deleted
                                             ? TimerStorageRpcMapper.ToDto(deleted)
                                             : null);

    public async UnaryResult<WipedPlayerDto> WipePlayerRunsAsync(ulong steamId, bool dryRun)
        => await RunAsync(async token => TimerStorageRpcMapper.ToDto(
                              await _storage.WipePlayerRunsAsync(steamId, dryRun, _options.StyleFactors, token)));

    public async UnaryResult<PlayerSummaryDto?> GetPlayerSummaryAsync(ulong steamId)
        => await RunAsync(async token => await _storage.GetPlayerSummaryAsync(steamId, token) is { } summary
                                             ? TimerStorageRpcMapper.ToDto(summary)
                                             : null);

    public async UnaryResult<Dictionary<ulong, float>> GetCompletedMapsAsync(ulong steamId, int style, int track)
        => await RunAsync(async token => new Dictionary<ulong, float>(
                              await _storage.GetCompletedMapsAsync(steamId, Style(style), Track(track), token)));

    public async UnaryResult<RankDto> GetPlayerPointsRankAsync(ulong steamId)
        => await RunAsync(async token =>
        {
            var (rank, total) = await _storage.GetPlayerPointsRankAsync(steamId, token);

            return new RankDto { Rank = rank, Total = total };
        });

    public async UnaryResult<bool> RegisterStyleFactorsAsync(Dictionary<int, double> factors)
        => await RunAsync(async token =>
        {
            if (factors is not { Count: > 0 } || !factors.ContainsKey(0)
                || factors.Any(f => unchecked((uint) f.Key) >= TimerConstants.MAX_STYLE || !double.IsFinite(f.Value) || f.Value is < 0 or > MaxStyleFactor))
            {
                throw TimerWriteRpcErrors.InvalidArgument();
            }

            if (_options.HasExplicitStyleFactors)
            {
                return false;
            }

            // Without credentials (Mode None) every caller is trusted as before; a Game-only key may add styles
            // nobody registered yet but cannot change the shared score policy.
            var caller = Context.CallContext.GetHttpContext().Features.Get<TimerWriteClient>();
            var mayOverwrite = caller is null || caller.Scopes.HasFlag(TimerWriteScopes.Admin);
            await _storage.SaveStyleFactorsAsync(factors, mayOverwrite, token);

            return _options.UseRegisteredStyleFactors(await _storage.GetStyleFactorsAsync(token));
        });

    public async UnaryResult<RankedPlayerDto[]> GetTopPlayersAsync(int limit)
        => await RunAsync(async token =>
        {
            var players = await _storage.GetTopPlayersAsync(Math.Clamp(limit, 1, MaxTopPlayers), token);
            var result  = new RankedPlayerDto[players.Count];

            for (var i = 0; i < result.Length; i++)
            {
                var p = players[i];
                result[i] = new RankedPlayerDto { SteamId = p.SteamId, Name = p.Name, Points = p.Points, Rank = p.Rank };
            }

            return result;
        });

    public async UnaryResult<PlayersRankDto> GetPlayersPointsRankAsync(ulong[] steamIds)
        => await RunAsync(async token =>
        {
            if (steamIds is not { Length: <= MaxRankedLookups })
            {
                throw TimerWriteRpcErrors.InvalidArgument();
            }

            var (players, total) = await _storage.GetPlayersPointsRankAsync(steamIds, token);

            return new PlayersRankDto
            {
                SteamIds = players.Keys.ToArray(),
                Ranks    = players.Values.Select(p => p.Rank).ToArray(),
                Points   = players.Values.Select(p => p.Points).ToArray(),
                Total    = total,
            };
        });

    public async UnaryResult UpdatePlayerMapStatsAsync(ulong steamId, string mapName, ulong workshopId, float deltaSeconds)
        => await RunAsync(token => Done(_storage.UpdatePlayerMapStatsAsync(steamId, Map(mapName), workshopId,
                                                                           Delta(deltaSeconds), token)));

    public async UnaryResult<MapStatsDto> GetPlayerMapStatsAsync(ulong steamId, string mapName, ulong workshopId)
        => await RunAsync(async token =>
        {
            var (playTime, playCount) = await _storage.GetPlayerMapStatsAsync(steamId, Map(mapName), workshopId, token);

            return new MapStatsDto { PlayTime = playTime, PlayCount = playCount };
        });

    public async UnaryResult<byte[]?> GetPlayerSettingsAsync(ulong steamId)
        => await RunAsync(token => _storage.GetPlayerSettingsAsync(Player(steamId), token));

    public async UnaryResult SavePlayerSettingsAsync(ulong steamId, byte[] data)
        => await RunAsync(token => Done(_storage.SavePlayerSettingsAsync(Player(steamId),
                                                                  data is { Length: <= MaxPlayerSettingsBytes }
                                                                      ? data
                                                                      : throw TimerWriteRpcErrors.InvalidArgument(),
                                                                  token)));

    public async UnaryResult<ZoneDto[]> GetZonesAsync(string mapName, ulong workshopId)
        => await RunAsync(async token => TimerStorageRpcMapper.ToDto(
                              await _storage.GetZonesAsync(Map(mapName), workshopId, token)));

    public async UnaryResult SaveZonesAsync(string mapName, ulong workshopId, ZoneDto[] zones)
        => await RunAsync(token => Done(_storage.SaveZonesAsync(Map(mapName), workshopId,
                                                                TimerStorageRpcMapper.ToZones(
                                                                    zones ?? throw TimerWriteRpcErrors.InvalidArgument()),
                                                                token)));

    public async UnaryResult<string?> GetReplayUrlAsync(string  mapName,
                                                        ulong   workshopId,
                                                        RunKind kind,
                                                        int     style,
                                                        int     track,
                                                        int     stage,
                                                        ulong?  steamId)
        => await RunAsync(token => _storage.GetReplayUrlAsync(Map(mapName), workshopId, Stage(kind), Style(style),
                                                              Track(track), StageNumber(stage), steamId, token));

    public async UnaryResult<string?> GetRunReplayUrlAsync(ulong runId)
        => await RunAsync(token => _storage.GetRunReplayUrlAsync(runId, token));

    public async UnaryResult<ulong[]> GetStoredReplayRunIdsAsync(ulong[] runIds)
        => await RunAsync(async token =>
        {
            if (runIds is null || runIds.Length == 0)
            {
                return [];
            }

            if (runIds.Length > MaxStoredReplayLookups)
            {
                throw TimerWriteRpcErrors.InvalidArgument();
            }

            var stored = await _storage.GetStoredReplayRunIdsAsync(runIds, token);
            var result = new ulong[stored.Count];

            for (var i = 0; i < result.Length; i++)
            {
                result[i] = stored[i];
            }

            return result;
        });

    public async UnaryResult<bool> SaveReplayUrlAsync(string mapName, ulong workshopId, ulong steamId, ulong runId, string url)
        => await RunAsync(token =>
        {
            if (!IsAllowedReplayUrl(url))
            {
                throw TimerWriteRpcErrors.InvalidArgument();
            }

            return _storage.SaveReplayUrlAsync(Map(mapName), workshopId, steamId, runId, url, token);
        });

    private static ulong Player(ulong steamId)
        => steamId != 0 ? steamId : throw TimerWriteRpcErrors.InvalidArgument();

    private static int Style(int style)
        => unchecked((uint)style) < TimerConstants.MAX_STYLE ? style : throw TimerWriteRpcErrors.InvalidArgument();

    private static int Track(int track)
        => unchecked((uint)track) < TimerConstants.MAX_TRACK ? track : throw TimerWriteRpcErrors.InvalidArgument();

    private static int StageNumber(int stage)
        => unchecked((uint)stage) < TimerConstants.MAX_STAGE ? stage : throw TimerWriteRpcErrors.InvalidArgument();

    // Storage enforces the upper bound (reported as InvalidArgument by Translate); reject the rest before a DB trip.
    private static float Delta(float deltaSeconds)
        => float.IsFinite(deltaSeconds) && deltaSeconds >= 0f
               ? deltaSeconds
               : throw TimerWriteRpcErrors.InvalidArgument();

    // Every game server later downloads this URL, so only http(s) and, when configured, the operator's replay store.
    private bool IsAllowedReplayUrl(string? url)
    {
        if (url is not { Length: > 0 and <= MaxReplayUrlLength }
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var prefixes = _options.ReplayUrlPrefixes;
        if (prefixes.Count == 0)
        {
            return true;
        }

        // Compare the normalized form so "https://store/../other" or encoded tricks cannot escape the prefix.
        var normalized = uri.AbsoluteUri;
        foreach (var prefix in prefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Map(string? mapName)
        => ApiRouteValidation.TryNormalizeMapName(mapName, out var normalized, out _)
               ? normalized
               : throw TimerWriteRpcErrors.InvalidArgument();

    private static bool Stage(RunKind kind)
        => kind switch
        {
            RunKind.Main  => false,
            RunKind.Stage => true,
            _             => throw TimerWriteRpcErrors.InvalidArgument(),
        };

    private static int Limit(int limit)
        => Math.Clamp(limit, 1, IRequestManager.DefaultRecordLimit);

    // Tier changes and recalculations queue boards under this instance's factors, as the CLI does.
    private IReadOnlyDictionary<int, double> ScorePolicy()
        => _options.HasScorePolicy
               ? _options.StyleFactors
               : throw TimerWriteRpcErrors.ScorePolicyNotConfigured();

    private static async Task<bool> Done(Task operation)
    {
        await operation;

        return true;
    }

    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> call)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Context.CallContext.CancellationToken);
        deadline.CancelAfter(_runtimeOptions.RequestTimeout);

        try
        {
            return await call(deadline.Token);
        }
        catch (RpcException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw deadline.IsCancellationRequested && !Context.CallContext.CancellationToken.IsCancellationRequested
                      ? new RpcException(new Status(StatusCode.DeadlineExceeded, "The request deadline elapsed."))
                      : TimerWriteRpcErrors.CancellationOrDeadline(Context.CallContext.Deadline);
        }
        catch (Exception exception)
        {
            throw TimerWriteRpcErrors.Translate(exception, _logger);
        }
    }
}

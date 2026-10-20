using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Enums;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Timer.Backend.Storage;

/// <summary>
/// The game server's reads and writes other than run submissions, on the owning
/// <see cref="TimerBackendStorage"/>'s scope and lifecycle. A map-scoped call binds the map to the
/// caller's workshop item before it resolves the map.
/// </summary>
public sealed class TimerBackendGameStorage
{
    private readonly TimerBackendStorage _owner;

    public TimerBackendGameStorage(TimerBackendStorage owner)
        => _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    private StorageServiceImpl Storage => _owner.Storage;

    public Task<MapProfile> GetMapInfoAsync(string mapName, ulong workshopId, CancellationToken cancellationToken = default)
        => OnMapAsync(mapName, workshopId, () => Storage.GetMapInfo(mapName), cancellationToken);

    public Task<IReadOnlyList<MapProfile>> GetMapProfilesAsync(CancellationToken cancellationToken = default)
        => _owner.ExecuteAsync(Storage.GetMapProfilesAsync, cancellationToken);

    public Task IncrementMapStatsAsync(string mapName, ulong workshopId, float deltaSeconds, CancellationToken cancellationToken = default)
        => OnMapAsync(mapName, workshopId, () => Done(Storage.IncrementMapStatsAsync(mapName, deltaSeconds)), cancellationToken);

    public Task<TimerBackendScoreAdministrationResult> SetMapTierAsync(string mapName,
                                                                       ulong  workshopId,
                                                                       byte   tier,
                                                                       IReadOnlyDictionary<int, double> styleFactors,
                                                                       CancellationToken cancellationToken = default)
        => OnMapAsync(mapName, workshopId,
                      () => Storage.SetMapTierAndRequeueScoresAsync(mapName, tier, styleFactors), cancellationToken);

    public Task<TimerBackendScoreAdministrationResult> SetMapRankedAsync(string mapName,
                                                                         ulong  workshopId,
                                                                         bool   ranked,
                                                                         IReadOnlyDictionary<int, double> styleFactors,
                                                                         CancellationToken cancellationToken = default)
        => OnMapAsync(mapName, workshopId,
                      () => Storage.SetMapRankedAndRequeueScoresAsync(mapName, ranked, styleFactors), cancellationToken);

    /// <summary>Requeues one map's boards, or every map's when <paramref name="mapName"/> is null.</summary>
    public Task<TimerBackendScoreAdministrationResult> RecalculateScoresAsync(string? mapName,
                                                                              ulong   workshopId,
                                                                              IReadOnlyDictionary<int, double> styleFactors,
                                                                              CancellationToken cancellationToken = default)
        => mapName is null
               ? _owner.ExecuteAsync(() => Storage.RequeueAllScorePoliciesAsync(styleFactors), cancellationToken)
               : OnMapAsync(mapName, workshopId,
                            () => Storage.RequeueMapScorePolicyAsync(mapName, styleFactors), cancellationToken);

    public Task<IReadOnlyList<RunRecord>> GetMapRecordsAsync(string mapName,
                                                             ulong  workshopId,
                                                             bool   stageRecords,
                                                             bool   allBoards,
                                                             int    style,
                                                             int    track,
                                                             int    stage,
                                                             int    limit,
                                                             CancellationToken cancellationToken = default)
        => OnMapAsync(mapName, workshopId, () => (stageRecords, allBoards) switch
        {
            (false, true)  => Storage.GetMapRecords(mapName, limit),
            (true, true)   => Storage.GetMapStageRecords(mapName, limit),
            (false, false) => Storage.GetMapRecords(mapName, style, track, limit),
            (true, false)  => Storage.GetMapStageRecords(mapName, style, track, stage, limit),
        }, cancellationToken);

    public Task<IReadOnlyList<RunRecord>> GetPlayerRecordsAsync(ulong  steamId,
                                                                string mapName,
                                                                ulong  workshopId,
                                                                bool   stageRecords,
                                                                CancellationToken cancellationToken = default)
        => OnMapAsync(mapName, workshopId,
                      () => stageRecords
                                ? Storage.GetPlayerStageRecords(new SteamID(steamId), mapName)
                                : Storage.GetPlayerRecords(new SteamID(steamId), mapName),
                      cancellationToken);

    public Task<IReadOnlyList<RunRecord>> GetRecentRecordsAsync(string mapName,
                                                                ulong  workshopId,
                                                                ulong  steamId,
                                                                int    limit,
                                                                CancellationToken cancellationToken = default)
        => OnMapAsync(mapName, workshopId,
                      () => Storage.GetRecentRecords(mapName, new SteamID(steamId), limit), cancellationToken);

    public Task<IReadOnlyList<RunRecord>> GetPlayerRunsAsync(string mapName,
                                                             ulong  workshopId,
                                                             ulong  steamId,
                                                             int    style,
                                                             int    track,
                                                             int    stage,
                                                             int    limit,
                                                             CancellationToken cancellationToken = default)
        => OnMapAsync(mapName, workshopId,
                      () => Storage.GetPlayerRuns(mapName, new SteamID(steamId), style, track, stage, limit),
                      cancellationToken);

    public Task RemoveMapRecordsAsync(string mapName, ulong workshopId, CancellationToken cancellationToken = default)
        => OnMapAsync(mapName, workshopId, () => Done(Storage.RemoveMapRecords(mapName)), cancellationToken);

    public Task<TimerBackendDeletedRun?> DeleteRunAsync(string                           mapName,
                                                        ulong                            workshopId,
                                                        ulong                            runId,
                                                        IReadOnlyDictionary<int, double> styleFactors,
                                                        CancellationToken                cancellationToken = default)
        => OnMapAsync(mapName, workshopId, () => Storage.DeleteRunAsync(mapName, runId, styleFactors), cancellationToken);

    public Task<TimerBackendWipedPlayer> WipePlayerRunsAsync(ulong                            steamId,
                                                             bool                             dryRun,
                                                             IReadOnlyDictionary<int, double> styleFactors,
                                                             CancellationToken                cancellationToken = default)
        => _owner.ExecuteAsync(() => Storage.WipePlayerRunsAsync(new SteamID(steamId), dryRun, styleFactors), cancellationToken);

    public Task<PlayerSummary?> GetPlayerSummaryAsync(ulong steamId, CancellationToken cancellationToken = default)
        => _owner.ExecuteAsync(() => Storage.GetPlayerSummary(new SteamID(steamId)), cancellationToken);

    public Task<IReadOnlyDictionary<ulong, float>> GetCompletedMapsAsync(ulong steamId,
                                                                         int   style,
                                                                         int   track,
                                                                         CancellationToken cancellationToken = default)
        => _owner.ExecuteAsync(() => Storage.GetCompletedMapsAsync(new SteamID(steamId), style, track), cancellationToken);

    public Task<IReadOnlyDictionary<int, double>> GetStyleFactorsAsync(CancellationToken cancellationToken = default)
        => _owner.ExecuteAsync(Storage.GetStyleFactorsAsync, cancellationToken);

    public Task SaveStyleFactorsAsync(IReadOnlyDictionary<int, double> factors,
                                      bool overwriteExisting = true,
                                      CancellationToken cancellationToken = default)
        => _owner.ExecuteAsync(() => Done(Storage.SaveStyleFactorsAsync(factors, overwriteExisting)), cancellationToken);

    public Task<IReadOnlyList<TimerBackendRankedPlayer>> GetTopPlayersAsync(int limit, CancellationToken cancellationToken = default)
        => _owner.ExecuteAsync(() => Storage.GetTopPlayersAsync(limit), cancellationToken);

    public Task<(IReadOnlyDictionary<ulong, (int Rank, uint Points)> players, int total)> GetPlayersPointsRankAsync(IReadOnlyList<ulong> steamIds,
                                                                                           CancellationToken cancellationToken = default)
        => _owner.ExecuteAsync(() => Storage.GetPlayersPointsRankAsync(steamIds), cancellationToken);

    public Task<(int rank, int total)> GetPlayerPointsRankAsync(ulong steamId, CancellationToken cancellationToken = default)
        => _owner.ExecuteAsync(() => Storage.GetPlayerPointsRank(new SteamID(steamId)), cancellationToken);

    public Task UpdatePlayerMapStatsAsync(ulong  steamId,
                                          string mapName,
                                          ulong  workshopId,
                                          float  deltaSeconds,
                                          CancellationToken cancellationToken = default)
        => OnMapAsync(mapName, workshopId,
                      () => Done(Storage.UpdatePlayerMapStatsAsync(new SteamID(steamId), mapName, deltaSeconds)),
                      cancellationToken);

    public Task<(float playTime, int playCount)> GetPlayerMapStatsAsync(ulong  steamId,
                                                                        string mapName,
                                                                        ulong  workshopId,
                                                                        CancellationToken cancellationToken = default)
        => OnMapAsync(mapName, workshopId,
                      () => Storage.GetPlayerMapStatsAsync(new SteamID(steamId), mapName), cancellationToken);

    public Task<byte[]?> GetPlayerSettingsAsync(ulong steamId, CancellationToken cancellationToken = default)
        => _owner.ExecuteAsync(() => Storage.GetPlayerSettingsAsync(new SteamID(steamId)), cancellationToken);

    public Task SavePlayerSettingsAsync(ulong steamId, byte[] data, CancellationToken cancellationToken = default)
        => _owner.ExecuteAsync(() => Done(Storage.SavePlayerSettingsAsync(new SteamID(steamId), data)), cancellationToken);

    public Task<IReadOnlyList<ZoneData>> GetZonesAsync(string mapName, ulong workshopId, CancellationToken cancellationToken = default)
        => OnMapAsync(mapName, workshopId, () => Storage.GetZonesAsync(mapName), cancellationToken);

    public Task SaveZonesAsync(string                  mapName,
                               ulong                   workshopId,
                               IReadOnlyList<ZoneData> zones,
                               CancellationToken       cancellationToken = default)
        => OnMapAsync(mapName, workshopId, () => Done(Storage.SaveZonesAsync(mapName, zones)), cancellationToken);

    public Task<string?> GetReplayUrlAsync(string mapName,
                                           ulong  workshopId,
                                           bool   stageRun,
                                           int    style,
                                           int    track,
                                           int    stage,
                                           ulong? steamId,
                                           CancellationToken cancellationToken = default)
        => OnMapAsync(mapName, workshopId,
                      () => Storage.GetReplayUrlAsync(mapName, stageRun ? RunType.Stage : RunType.Main,
                                                      style, track, stageRun ? stage : 0, steamId),
                      cancellationToken);

    public Task<string?> GetRunReplayUrlAsync(ulong runId, CancellationToken cancellationToken = default)
        => _owner.ExecuteAsync(() => Storage.GetRunReplayUrlAsync(runId), cancellationToken);

    public Task<IReadOnlyList<ulong>> GetStoredReplayRunIdsAsync(IReadOnlyList<ulong> runIds,
                                                                 CancellationToken    cancellationToken = default)
        => _owner.ExecuteAsync(() => Storage.GetStoredReplayRunIdsAsync(runIds), cancellationToken);

    public Task<bool> SaveReplayUrlAsync(string mapName,
                                         ulong  workshopId,
                                         ulong  steamId,
                                         ulong  runId,
                                         string url,
                                         CancellationToken cancellationToken = default)
        => OnMapAsync(mapName, workshopId,
                      () => Storage.SaveReplayUrlAsync(mapName, steamId, runId, url), cancellationToken);

    // The binding is flow-scoped: set inside the async operation, it ends with it.
    private Task<T> OnMapAsync<T>(string mapName, ulong workshopId, Func<Task<T>> operation, CancellationToken cancellationToken)
        => _owner.ExecuteAsync(async () =>
        {
            Storage.SetMapWorkshopId(mapName, workshopId);

            return await operation();
        }, cancellationToken);

    private static async Task<bool> Done(Task operation)
    {
        await operation;

        return true;
    }
}

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using SqlSugar;

namespace Timer.Backend.Storage;

internal sealed partial class StorageServiceImpl
{
    private ISugarQueryable<PlayerBestRunEntity> QueryBestRuns() =>
        _db.Queryable<PlayerBestRunEntity>();

    private async Task EnsureBestRunsSeededForMapAsync(ulong mapId, RunType runType)
    {
        var seedKey = (mapId, runType);

        if (_bestRunMapSeededCache.ContainsKey(seedKey))
        {
            return;
        }

        // Map-wide and scoped seeds share a gate. Cache entries mean completed work,
        // so concurrent readers cannot observe a partially populated best-run table.
        var gate = _bestRunSeedLocks.GetOrAdd(seedKey, _ => new SemaphoreSlim(1, 1));
        await WaitSeedGateAsync(gate);

        try
        {
            if (_bestRunMapSeededCache.ContainsKey(seedKey))
            {
                return;
            }

            if (await IsMapSeededAsync(mapId, runType))
            {
                _bestRunMapSeededCache.TryAdd(seedKey, 0);
                return;
            }

            await WithRecordTransactionAsync(async () =>
            {
                await LockMapAsync(mapId);
                var baseQuery = _db.Queryable<RunEntity>()
                                   .Where(x => x.MapId      == mapId
                                               && x.RunType == runType);

                if (runType == RunType.Main)
                {
                    baseQuery = baseQuery.Where(x => x.Stage == 0);
                }
                else
                {
                    baseQuery = baseQuery.Where(x => x.Stage > 0);
                }

                var rows = await QuerySeedBestRows(baseQuery).ToListAsync(OperationCancellation);
                await UpsertSeedBestRowsAsync(mapId, runType, rows);

                // In the seed's transaction, under the map lock: the flags read here are current.
                var seeded = await _db.Queryable<MapEntity>().Where(x => x.MapId == mapId)
                                      .Select(x => x.BestRunsSeeded).FirstAsync(OperationCancellation);
                var marked = seeded | SeededFlag(runType);
                await _db.Updateable<MapEntity>().SetColumns(x => x.BestRunsSeeded == marked)
                         .Where(x => x.MapId == mapId).ExecuteCommandAsync(OperationCancellation);
            });
            _bestRunMapSeededCache.TryAdd(seedKey, 0);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task EnsureBestRunsSeededAsync(ulong mapId, RunType runType, int style, ushort track, ushort stage)
    {
        if (_bestRunMapSeededCache.ContainsKey((mapId, runType)))
        {
            return;
        }

        var seedKey = (mapId, runType, style, track, stage);

        if (_bestRunSeededCache.ContainsKey(seedKey))
        {
            return;
        }

        var gate = _bestRunSeedLocks.GetOrAdd((mapId, runType), _ => new SemaphoreSlim(1, 1));
        await WaitSeedGateAsync(gate);

        try
        {
            if (_bestRunMapSeededCache.ContainsKey((mapId, runType)) || _bestRunSeededCache.ContainsKey(seedKey))
            {
                return;
            }

            if (await IsMapSeededAsync(mapId, runType))
            {
                _bestRunMapSeededCache.TryAdd((mapId, runType), 0);
                return;
            }

            await WithRecordTransactionAsync(async () =>
            {
                await LockMapAsync(mapId);
                // An existing row does not prove that every player has been seeded.
                var baseQuery = _db.Queryable<RunEntity>()
                                    .Where(x => x.MapId      == mapId
                                                && x.RunType == runType
                                                && x.Style   == style
                                                && x.Track   == track
                                                && x.Stage   == stage);
                var rows = await QuerySeedBestRows(baseQuery).ToListAsync(OperationCancellation);
                await UpsertSeedBestRowsAsync(mapId, runType, rows, style, track, stage);
            });
            _bestRunSeededCache.TryAdd(seedKey, 0);
        }
        finally
        {
            gate.Release();
        }
    }

    // A seed can run for seconds on a large map. Waiters must not hold their operation's pooled connection
    // meanwhile: a cold-cache burst on one map would otherwise pin a connection per waiter and starve every
    // other request of the pool. The uncontended path (Wait(0)) costs nothing extra.
    private async Task WaitSeedGateAsync(SemaphoreSlim gate)
    {
        // Only an operation's own client holds its connection open for the whole operation; the shared root
        // client (startup, CLI, tests) auto-closes after each statement and has nothing to release. It is also
        // shared by concurrent callers, so its Transaction can be another caller's seed and is not checked.
        var operation = _operationDb.Value;

        // Seeds run their own map-locked transaction on this client, so seed gates are always acquired before
        // any transaction or database lock. Enforce it: a caller inside a transaction could neither release its
        // connection below nor seed safely.
        if (operation?.Ado.Transaction is not null)
        {
            throw new InvalidOperationException("Best-run seed gates must be acquired outside a transaction.");
        }

        if (gate.Wait(0))
        {
            return;
        }

        var connection = operation?.Ado.Connection as DbConnection;
        var reopen = connection is { State: ConnectionState.Open };
        if (reopen)
        {
            await connection!.CloseAsync();
        }

        await gate.WaitAsync(OperationCancellation);

        if (reopen)
        {
            try
            {
                await connection!.OpenAsync(OperationCancellation);
            }
            catch
            {
                gate.Release();
                throw;
            }
        }
    }

    private static int SeededFlag(RunType runType) => runType == RunType.Main ? 1 : 2;

    private async Task<bool> IsMapSeededAsync(ulong mapId, RunType runType)
    {
        var seeded = await _db.Queryable<MapEntity>().Where(x => x.MapId == mapId)
                              .Select(x => x.BestRunsSeeded).FirstAsync(OperationCancellation);

        return (seeded & SeededFlag(runType)) != 0;
    }

    private static ISugarQueryable<SeedBestRunRow> QuerySeedBestRows(ISugarQueryable<RunEntity> query)
        => query.Select(x => new SeedBestRunRow
                {
                    SteamId = x.SteamId,
                    Style = x.Style,
                    Track = x.Track,
                    Stage = x.Stage,
                    RunId = x.Id,
                    BestTime = x.Time,
                    BestTicks = x.Ticks,
                    // Member expressions are required here: nameof strings become SQL
                    // parameters, which partition/order by constants and pick arbitrary runs.
                    RowNum = SqlFunc.RowNumber($"{x.Time} ASC, {x.Id} ASC", $"{x.Style}, {x.Track}, {x.Stage}, {x.SteamId}"),
                })
                .MergeTable()
                .Where(x => x.RowNum == 1);

    private async Task UpsertSeedBestRowsAsync(ulong mapId,
                                               RunType runType,
                                               List<SeedBestRunRow> rows,
                                               int? style = null,
                                               ushort? track = null,
                                               ushort? stage = null)
    {
        if (rows.Count == 0)
        {
            return;
        }

        // Skip unchanged rows in memory so a cold map read does not issue one
        // update per player. The map lock protects this comparison until commit.
        var existingQuery = QueryBestRuns().Where(x => x.MapId == mapId && x.RunType == runType);
        if (style is not null && track is not null && stage is not null)
        {
            var scopedStyle = style.Value;
            var scopedTrack = track.Value;
            var scopedStage = stage.Value;
            existingQuery = existingQuery.Where(x => x.Style == scopedStyle
                                                     && x.Track == scopedTrack
                                                     && x.Stage == scopedStage);
        }

        var existingRows = await existingQuery.ToListAsync(OperationCancellation);
        var existing = new Dictionary<(long steamId, int style, ushort track, ushort stage), PlayerBestRunEntity>(existingRows.Count);
        foreach (var best in existingRows)
        {
            existing[(best.SteamId, best.Style, best.Track, best.Stage)] = best;
        }

        var now      = DateTime.UtcNow;
        var inserts = new List<PlayerBestRunEntity>();
        var updates = new List<PlayerBestRunEntity>();

        foreach (var row in rows)
        {
            if (existing.TryGetValue((row.SteamId, row.Style, row.Track, row.Stage), out var best)
                && CompareTimes(best.BestTicks, best.BestTime, row.BestTicks, row.BestTime) is var order
                && (order < 0 || (order == 0 && best.RunId <= row.RunId)))
            {
                continue;
            }

            (best is null ? inserts : updates).Add(new PlayerBestRunEntity
            {
                Id        = best?.Id ?? 0,
                SteamId   = row.SteamId,
                MapId     = mapId,
                RunType   = runType,
                Stage     = row.Stage,
                Style     = row.Style,
                Track     = row.Track,
                RunId     = row.RunId,
                BestTime  = TimeOf(row.BestTicks, row.BestTime),
                BestTicks = row.BestTicks,
                UpdatedAtUnixMilliseconds = ToUnixTimeMilliseconds(now),
            });
        }

        // The map lock makes the comparison above authoritative. Write only
        // improvements, in bounded batches, without a second existence probe.
        foreach (var batch in inserts.Chunk(500))
            await _db.Insertable(batch).ExecuteCommandAsync(OperationCancellation);
        foreach (var batch in updates.Chunk(500))
            await _db.Updateable(batch).UpdateColumns(x => new { x.RunId, x.BestTime, x.BestTicks, x.UpdatedAtUnixMilliseconds }).ExecuteCommandAsync(OperationCancellation);
    }

    private static int CompareTimes(int aTicks, float aTime, int bTicks, float bTime)
        => TimeOf(aTicks, aTime).CompareTo(TimeOf(bTicks, bTime));

    private void RemoveBestRunSeedCacheForMap(ulong mapId)
    {
        // Iterate the ConcurrentDictionary struct enumerators directly; .Keys would snapshot each entire
        // keyset into a fresh List + ReadOnlyCollection per call. The enumerator allocates nothing and is
        // safe to remove from while enumerating.
        foreach (var kvp in _bestRunMapSeededCache)
        {
            if (kvp.Key.mapId == mapId)
            {
                _bestRunMapSeededCache.TryRemove(kvp.Key, out _);
            }
        }

        foreach (var kvp in _bestRunSeededCache)
        {
            if (kvp.Key.mapId == mapId)
            {
                _bestRunSeededCache.TryRemove(kvp.Key, out _);
            }
        }
    }

    private sealed class SeedBestRunRow
    {
        [SugarColumn(ColumnDataType = "bigint")]
        public long SteamId { get; set; }

        public int Style { get; set; }

        public ushort Track { get; set; }

        public ushort Stage { get; set; }

        public ulong RunId { get; set; }

        public float BestTime { get; set; }

        public int BestTicks { get; set; }

        [SugarColumn(IsOnlyIgnoreInsert = true, IsOnlyIgnoreUpdate = true)]
        public int RowNum { get; set; }
    }
}

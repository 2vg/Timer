using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Storage.Tests;

public sealed class CachedSqlTests
{
    private const ulong Sentinel = 9_100_000_000_000_001;

    [Fact]
    public void ArgumentsBindToTheParametersThatHeldTheirSentinels()
    {
        var read = CachedSql.Create(new("SELECT 1 WHERE a = @a AND b = @b AND c = @c",
                                         [new("@a", Sentinel), new("@b", 3), new("@c", Sentinel)]),
                                     [Sentinel]);

        var bound = read.Bind([42UL]);

        Assert.Equal([42UL, 3, 42UL], bound.Select(x => x.Value));
    }

    [Fact]
    public void AnArgumentWrittenIntoTheSqlIsRejected()
        => Assert.Throws<InvalidOperationException>(() =>
            CachedSql.Create(new($"SELECT 1 WHERE a = {Sentinel}", []), [Sentinel]));

    [Fact]
    public void DatesBindAndConstantsKeepTheirParameterTyping()
    {
        var sentinel = new DateTime(2099, 1, 2, 3, 4, 9, DateTimeKind.Utc);
        var none = new SugarParameter("@b", null) { DbType = System.Data.DbType.DateTime, IsNullable = true, TypeName = "timestamp" };
        var statement = CachedSql.Create(new("UPDATE t SET a = @a, b = @b", [new("@a", sentinel), none]), [sentinel]);
        var now = DateTime.UtcNow;

        var bound = statement.Bind([now]);

        Assert.Equal(now, bound[0].Value);
        Assert.Equal((null, System.Data.DbType.DateTime, true, "timestamp"),
                     (bound[1].Value, bound[1].DbType, bound[1].IsNullable, bound[1].TypeName));
    }

    [Fact]
    public void ADateWrittenIntoTheSqlIsRejected()
        => Assert.Throws<InvalidOperationException>(() =>
            CachedSql.Create(new("UPDATE t SET a = '2099-01-02 03:04:09'", []), [new DateTime(2099, 1, 2, 3, 4, 9)]));

    [Fact]
    public void AnArgumentNoParameterHoldsIsRejected()
        => Assert.Throws<InvalidOperationException>(() =>
            CachedSql.Create(new("SELECT 1 WHERE a = @a", [new("@a", 5)]), [Sentinel]));

    [Fact]
    public Task SqliteStatementsReuseTheirShapesWithNewValues() => RunSuite(DbType.Sqlite, null);

    [DisposableDatabaseFact("TIMER_TEST_MYSQL")]
    public Task MySqlStatementsReuseTheirShapesWithNewValues() => RunSuite(DbType.MySql, "TIMER_TEST_MYSQL");

    [DisposableDatabaseFact("TIMER_TEST_POSTGRES")]
    public Task PostgreSqlStatementsReuseTheirShapesWithNewValues() => RunSuite(DbType.PostgreSQL, "TIMER_TEST_POSTGRES");

    // Every statement alternates between maps, players, runs and boards, so a shape that kept its first values would show.
    private static async Task RunSuite(DbType type, string? variable)
    {
        var path       = Path.Combine(Path.GetTempPath(), $"timer-cached-read-{Guid.NewGuid():N}.db");
        var connection = type == DbType.Sqlite ? $"Data Source={path};Pooling=False" : Environment.GetEnvironmentVariable(variable!)!;
        if (type != DbType.Sqlite)
        {
            var parsed = new DbConnectionStringBuilder { ConnectionString = connection };
            Assert.Contains("test", Convert.ToString(parsed["Database"])!, StringComparison.OrdinalIgnoreCase);
        }

        var store = new StorageServiceImpl(type, connection, NullLogger<StorageServiceImpl>.Instance, false);
        if (type == DbType.Sqlite)
        {
            store.Db.CurrentConnectionConfig.ConfigureExternalServices.EntityService = (_, column) =>
            {
                if (column.IsIdentity) column.DataType = "INTEGER";
            };
        }
        else
        {
            store.Db.DbMaintenance.CreateDatabase();
        }

        try
        {
            store.Init(startScoreRecalcWorker: false);
            await Exercise(store);
        }
        finally
        {
            store.Shutdown();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static async Task Exercise(StorageServiceImpl store)
    {
        var a = $"surf_cached_{Guid.NewGuid():N}";
        var b = $"surf_cached_{Guid.NewGuid():N}";
        var p = new SteamID(76561198000000000UL + (ulong)Random.Shared.NextInt64(1, 500_000_000));
        var q = new SteamID(p.AsPrimitive() + 1);

        await store.GetPlayerProfile(p, "Alpha");
        await store.GetPlayerProfile(q, "Beta");
        var (_, pa, _) = await store.AddPlayerRecord(p, a, Run(60, 1));
        await store.AddPlayerRecord(q, a, Run(70, 0));
        var (_, qb, _) = await store.AddPlayerRecord(q, b, Run(50, 2));
        await store.AddPlayerStageRecord(p, a, new RecordRequest { Stage = 1, Time = 10 });

        for (var round = 0; round < 2; round++)
        {
            Assert.Equal([(p.AsPrimitive(), "Alpha", 60f), (q.AsPrimitive(), "Beta", 70f)],
                         (await store.GetMapRecords(a, 0, 0)).Select(x => (x.SteamId, x.PlayerName, x.Time)));
            Assert.Equal([(q.AsPrimitive(), 50f)], (await store.GetMapRecords(b, 0, 0)).Select(x => (x.SteamId, x.Time)));
            Assert.Equal(2, (await store.GetMapRecords(a)).Count);
            // HTTP limits share a few cached sizes; the reader stops at the requested count.
            Assert.Equal([60f], (await store.GetMapRecordsForReadApiAsync(a, false, 0, 0, 0, 1, true)).Select(x => x.Time));
            Assert.Equal([60f, 70f], (await store.GetMapRecordsForReadApiAsync(a, false, 0, 0, 0, 7, true)).Select(x => x.Time));
            Assert.Equal(1, Assert.Single(await store.GetMapStageRecords(a)).Stage);
            Assert.Empty(await store.GetMapStageRecords(b));

            Assert.Equal(60f, Assert.Single(await store.GetPlayerRecords(p, a)).Time);
            Assert.Equal(70f, Assert.Single(await store.GetPlayerRecords(q, a)).Time);
            Assert.Equal(50f, Assert.Single(await store.GetPlayerRecords(q, b)).Time);
            Assert.Empty(await store.GetPlayerRecords(p, b));
            Assert.Equal(10f, Assert.Single(await store.GetPlayerStageRecords(p, a)).Time);

            Assert.Single(await store.GetRecordCheckpoints(pa.Id));
            Assert.Equal([1u, 2u], (await store.GetRecordCheckpoints(qb.Id)).Select(x => x.CheckpointIndex));
        }

        await store.UpdatePlayerMapStatsAsync(p, a, 5);
        await store.UpdatePlayerMapStatsAsync(q, a, 7);
        Assert.Equal((5f, 1), await store.GetPlayerMapStatsAsync(p, a));
        Assert.Equal((7f, 1), await store.GetPlayerMapStatsAsync(q, a));
        Assert.Equal((0f, 0), await store.GetPlayerMapStatsAsync(p, b));

        // Random per run, so players left in a reused test database never tie with these two.
        var top = 3_000_000_000u + (uint)Random.Shared.Next(1_000_000_000);
        foreach (var (player, points) in new[] { (p, top), (q, top - 1) })
        {
            var id = unchecked((long)player.AsPrimitive());
            await store.Db.Updateable<PlayerEntity>().SetColumns(x => x.Points == points).Where(x => x.SteamId == id).ExecuteCommandAsync();
        }

        var (rankP, totalP) = await store.GetPlayerPointsRank(p);
        var (rankQ, totalQ) = await store.GetPlayerPointsRank(q);
        Assert.Equal(rankP + 1, rankQ);
        Assert.Equal(totalP, totalQ);

        // Map-start loads cap each board, so a long map's later boards are never cut off.
        var e = $"surf_cached_{Guid.NewGuid():N}";
        foreach (var (player, offset) in new[] { (p, 0f), (q, 1f) })
        {
            for (var s = 1; s <= 3; s++)
                await store.AddPlayerStageRecord(player, e, new RecordRequest { Stage = s, Time = 10 * s + offset });
            await store.AddPlayerRecord(player, e, new RecordRequest { Track = 1, Time = 30 + offset });
            await store.AddPlayerRecord(player, e, new RecordRequest { Time = 60 + offset });
        }

        Assert.Equal([(1, 10f), (2, 20f), (3, 30f)], (await store.GetMapStageRecords(e, 1)).Select(x => (x.Stage, x.Time)));
        Assert.Equal([(1, 10f)], (await store.GetMapStageRecords(a, 1)).Select(x => (x.Stage, x.Time)));
        Assert.Equal([(0, 60f), (1, 30f)], (await store.GetMapRecords(e, 1)).Select(x => (x.Track, x.Time)));
        Assert.Equal(6, (await store.GetMapStageRecords(e, 5)).Count);

        // Each record merges into its board's score queue through the one cached update.
        var c = $"surf_cached_{Guid.NewGuid():N}";
        var d = $"surf_cached_{Guid.NewGuid():N}";
        await store.AddPlayerRecord(p, c, new RecordRequest { Time = 60 });
        await store.AddPlayerRecord(q, d, new RecordRequest { Style = 1, Track = 2, Time = 70, StyleFactor = 0.5 });
        var first = await Queue(store, c, 0, 0);
        await store.AddPlayerRecord(p, c, new RecordRequest { Time = 50 });
        await store.AddPlayerRecord(q, d, new RecordRequest { Style = 1, Track = 2, Time = 65, StyleFactor = 0.5 });
        await store.AddPlayerRecord(p, c, new RecordRequest { Time = 40, StyleFactor = 2 });

        var queueC = await Queue(store, c, 0, 0);
        var queueD = await Queue(store, d, 1, 2);
        Assert.Equal((3L, 2.0), (queueC.RequestedGeneration, queueC.StyleFactor));
        Assert.Equal((2L, 0.5), (queueD.RequestedGeneration, queueD.StyleFactor));
        Assert.Equal(first.AvailableAtUtc, queueC.AvailableAtUtc);
        Assert.Equal(first.PendingSinceUtc, queueC.PendingSinceUtc);

        // A recalc re-totals only players whose board score changed; recalc-scores asks for a repair of every one.
        await Drain(store);
        await AssertTotalsMatchScores(store, p, q);
        var mapC = (await store.GetMapInfo(c)).MapId;
        var mapD = (await store.GetMapInfo(d)).MapId;
        foreach (var player in new[] { p, q })
        {
            var id = unchecked((long)player.AsPrimitive());
            await store.Db.Updateable<PlayerEntity>().SetColumns(x => x.Points == 7u).Where(x => x.SteamId == id).ExecuteCommandAsync();
        }

        await store.EnqueueScoreRecalcAsync(mapC, 0, 0, 2);
        await Drain(store);
        Assert.Equal(7u, (await Profile(store, p)).Points);

        await store.EnqueueScoreRecalcAsync(mapC, 0, 0, 2, repairTotals: true);
        await store.EnqueueScoreRecalcAsync(mapD, 1, 2, 0.5, repairTotals: true);
        Assert.True((await Queue(store, c, 0, 0)) is { RepairGeneration: > 0 } queuedC && queuedC.RepairGeneration == queuedC.RequestedGeneration);
        Assert.True((await Queue(store, d, 1, 2)) is { RepairGeneration: > 0 } queuedD && queuedD.RepairGeneration == queuedD.RequestedGeneration);
        await Drain(store);
        await AssertTotalsMatchScores(store, p, q);
    }

    private static async Task Drain(StorageServiceImpl store)
    {
        while (await store.ProcessScoreRecalcOutboxBatchAsync(DateTime.UtcNow.AddMinutes(10), $"cached-{Guid.NewGuid():N}") > 0)
        {
        }
    }

    private static Task<PlayerEntity> Profile(StorageServiceImpl store, SteamID player)
    {
        var id = unchecked((long)player.AsPrimitive());

        return store.Db.Queryable<PlayerEntity>().Where(x => x.SteamId == id).FirstAsync();
    }

    private static async Task AssertTotalsMatchScores(StorageServiceImpl store, params SteamID[] players)
    {
        foreach (var player in players)
        {
            var id = unchecked((long)player.AsPrimitive());
            var scores = await store.Db.Queryable<PlayerTrackScoreEntity>().Where(x => x.SteamId == id).ToListAsync();
            Assert.NotEmpty(scores);
            Assert.Equal(scores.Sum(x => (long)x.Points), (long)(await Profile(store, player)).Points);
        }
    }

    private static async Task<ScoreRecalcOutboxEntity> Queue(StorageServiceImpl store, string map, int style, ushort track)
    {
        var mapId = (await store.GetMapInfo(map)).MapId;

        return await store.Db.Queryable<ScoreRecalcOutboxEntity>()
                          .Where(x => x.MapId == mapId && x.Style == style && x.Track == track)
                          .SingleAsync();
    }

    private static RecordRequest Run(float time, int checkpoints)
    {
        var run = new RecordRequest { Time = time };
        for (var i = 1; i <= checkpoints; i++)
        {
            run.Checkpoints.Add(new RecordRequest.CheckpointRecord { CheckpointIndex = i, Time = time * i / (checkpoints + 1) });
        }

        return run;
    }

    private sealed class DisposableDatabaseFactAttribute : FactAttribute
    {
        public DisposableDatabaseFactAttribute(string variable)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
                Skip = $"Set {variable} to a disposable loopback test database.";
        }
    }
}

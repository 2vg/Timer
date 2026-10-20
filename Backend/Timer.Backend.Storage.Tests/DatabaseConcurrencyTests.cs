using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.Backend.Storage;
using Xunit;
using Xunit.Abstractions;

namespace Timer.Backend.Storage.Tests;

// These tests require an explicitly supplied disposable database. They create
// uniquely named maps/players and never connect to application configuration.
// Outbox workers scan, claim and count the whole score-recalc Outbox, and the lock barriers
// assume no competing load: tests running alongside that queue score work for their own maps
// (any personal best does) make claims, counts and barrier timings nondeterministic.
[Collection(SqlSchemaMutationCollection.Name)]
public sealed class DatabaseConcurrencyTests(ITestOutputHelper output)
{
    [DatabaseFact("TIMER_TEST_MYSQL")]
    public Task MySqlConcurrencyAndBatching() => RunSuite(DbType.MySql, "TIMER_TEST_MYSQL");

    [DatabaseFact("TIMER_TEST_POSTGRES")]
    public Task PostgreSqlConcurrencyAndBatching() => RunSuite(DbType.PostgreSQL, "TIMER_TEST_POSTGRES");

    private async Task RunSuite(DbType dialect, string environment)
    {
        var stores = new List<StorageServiceImpl>();
        StorageServiceImpl NewStore()
        {
            var store = new StorageServiceImpl(dialect,
                                               Environment.GetEnvironmentVariable(environment)!,
                                               NullLogger<StorageServiceImpl>.Instance,
                                               enableScoreRecalcWorker: false);
            stores.Add(store);
            return store;
        }
        var first = NewStore();
        var second = NewStore();
        var third = NewStore();
        try
        {
            // The opt-in integration database is explicitly disposable, but it may be reused
            // across local runs. Outbox workers scan globally, so stale pending rows from an
            // interrupted prior test would make claim-count assertions nondeterministic.
            await first.Db.Deleteable<ScoreRecalcOutboxEntity>().ExecuteCommandAsync();
            var tag = Guid.NewGuid().ToString("N");
            var player = new SteamID(76561198000000000UL + (ulong)Random.Shared.NextInt64(1, 1000000000));
            var playerValue = checked((long)player.AsPrimitive());
            await first.GetPlayerProfile(player, "Concurrency test");
            var mapA = await first.GetMapInfo($"surf_test_{tag}_a");
            var mapB = await first.GetMapInfo($"surf_test_{tag}_b");
            var mapC = await first.GetMapInfo($"surf_test_{tag}_c");
            foreach (var map in new[] { mapA, mapB, mapC })
            {
                await first.Db.Insertable(new RunEntity
                {
                    MapId = map.MapId, SteamId = unchecked((long)player.AsPrimitive()), RunType = RunType.Main, Time = 80,
                    DateUnixTimeMilliseconds = StorageServiceImpl.ToUnixTimeMilliseconds(DateTime.UtcNow),
                }).ExecuteCommandAsync();
            }

            // Two processes seeding the same absent best row must both succeed.
            await Task.WhenAll(Task.Run(() => first.GetPlayerRecords(player, mapA.MapName)),
                               Task.Run(() => second.GetPlayerRecords(player, mapA.MapName)));
            Assert.Equal(1, await first.Db.Queryable<PlayerBestRunEntity>().Where(x => x.MapId == mapA.MapId).CountAsync());

            // Simultaneous first finishes for one player: no unique-key exception
            // may abort a PostgreSQL run transaction, and equal time uses lower ID.
            var finishers = Enumerable.Range(0, 8).Select(_ => NewStore()).ToArray();
            var newPlayer = new SteamID(player.AsPrimitive() + 3000000000UL);
            var profiles = await Task.WhenAll(finishers.Select(store => Task.Run(() => store.GetPlayerProfile(newPlayer, "Concurrent join"))));
            Assert.All(profiles, profile => Assert.True(profile.Id > 0));
            Assert.Single(profiles.Select(x => x.Id).Distinct());
            var saves = await Task.WhenAll(finishers.Select((store, i) => Task.Run(() =>
                store.AddPlayerStageRecord(player, mapA.MapName, new RecordRequest { Stage = 1, Time = 60 + i % 3 }))));
            var best = await first.Db.Queryable<PlayerBestRunEntity>().Where(x => x.MapId == mapA.MapId && x.Stage == 1).FirstAsync();
            Assert.Equal(60, best.BestTime);
            Assert.Equal((ulong)saves.Where(x => x.Item2.Time == 60).Min(x => x.Item2.Id), best.RunId);
            Assert.Equal(8, await first.Db.Queryable<RunEntity>().Where(x => x.MapId == mapA.MapId && x.Stage == 1).CountAsync());
            output.WriteLine($"{dialect}: concurrent cold seed and 8 first finishes passed.");

            // A finish waits for an active seed, then compares with the committed
            // best row. The SQL-only read paths do not acquire these write locks.
            await first.Db.Insertable(new RunEntity
            {
                MapId = mapB.MapId, SteamId = unchecked((long)player.AsPrimitive()), RunType = RunType.Stage, Stage = 1, Time = 80,
                DateUnixTimeMilliseconds = StorageServiceImpl.ToUnixTimeMilliseconds(DateTime.UtcNow),
            }).ExecuteCommandAsync();
            using (var hold = new SqlPause(first, IsBestRunWrite))
            {
                var seed = Task.Run(() => first.GetPlayerStageRecords(player, mapB.MapName));
                Task? finish = null;
                try
                {
                    await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    finish = Task.Run(() => second.AddPlayerStageRecord(player, mapB.MapName, new RecordRequest { Stage = 1, Time = 60 }));
                    await Task.Delay(100);
                    Assert.False(finish.IsCompleted);
                }
                finally { hold.Release.Set(); await seed; if (finish is not null) await finish; }
            }
            Assert.Equal(60, (await first.Db.Queryable<PlayerBestRunEntity>().Where(x => x.MapId == mapB.MapId && x.Stage == 1).FirstAsync()).BestTime);

            // A recalculation takes no map lock: while one holds its player locks and score
            // writes, a finish on the same map still commits.
            await second.GetPlayerRecords(player, mapB.MapName);
            await third.GetPlayerRecords(player, mapC.MapName);
            using (var hold = new SqlPause(first, IsPlayerUpdate))
            {
                var a = Task.Run(() => first.RecalculateTrackScoresAsync(mapA.MapId, 0, 0, 1));
                try
                {
                    await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    await second.AddPlayerStageRecord(player, mapA.MapName, new RecordRequest { Stage = 2, Time = 50 })
                                .WaitAsync(TimeSpan.FromSeconds(15));
                }
                finally { hold.Release.Set(); await a; }
            }
            await third.RecalculateTrackScoresAsync(mapC.MapId, 0, 0, 1);

            // Different maps with one overlapping player: B must wait until A's
            // total commits, then sum the newly committed A score in a fresh snapshot.
            await first.Db.Updateable<MapEntity>().SetColumns(x => x.BasePot == 2000).Where(x => x.MapId == mapA.MapId).ExecuteCommandAsync();
            // Saving map metadata preserves BasePot and replaces bonus tiers.
            mapA.Tier = [1, 2];
            await first.UpdateMapInfo(mapA);
            Assert.Equal(2000, (await first.Db.Queryable<MapEntity>().Where(x => x.MapId == mapA.MapId).FirstAsync()).BasePot);
            Assert.Equal((byte)2, (await first.Db.Queryable<MapTrackEntity>().Where(x => x.MapId == mapA.MapId && x.Track == 1).FirstAsync()).Tier);
            using (var hold = new SqlPause(first, IsPlayerUpdate))
            using (var attempted = new SqlSignal(second, sql => sql.Contains("surf_players") && sql.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase)))
            {
                var a = Task.Run(() => first.RecalculateTrackScoresAsync(mapA.MapId, 0, 0, 1));
                Task? b = null;
                try
                {
                    await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    b = Task.Run(() => second.RecalculateTrackScoresAsync(mapB.MapId, 0, 0, 1));
                    await attempted.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    await Task.Delay(100);
                    Assert.False(b.IsCompleted);
                }
                finally { hold.Release.Set(); await a; if (b is not null) await b; }
            }
            Assert.Equal(4000u, (await first.Db.Queryable<PlayerEntity>().Where(x => x.SteamId == playerValue).FirstAsync()).Points);

            second.Db.Aop.OnLogExecuting = (sql, _) => { if (IsPlayerUpdate(sql)) throw new InvalidOperationException("Injected wipe failure"); };
            await Assert.ThrowsAnyAsync<Exception>(() => second.RemoveMapRecords(mapA.MapName));
            second.Db.Aop.OnLogExecuting = null;
            Assert.Equal(10, await first.Db.Queryable<RunEntity>().Where(x => x.MapId == mapA.MapId).CountAsync());
            Assert.Equal(2000u, (await first.Db.Queryable<PlayerTrackScoreEntity>().Where(x => x.MapId == mapA.MapId).FirstAsync()).Points);
            output.WriteLine($"{dialect}: map locks, cross-map player locks and refreshed score configuration passed.");

            // A failure after score writes rolls back both details and totals.
            await first.Db.Updateable<MapEntity>().SetColumns(x => x.BasePot == 3000).Where(x => x.MapId == mapA.MapId).ExecuteCommandAsync();
            first.Db.Aop.OnLogExecuting = (sql, _) => { if (IsPlayerUpdate(sql)) throw new InvalidOperationException("Injected total failure"); };
            await Assert.ThrowsAnyAsync<Exception>(() => first.RecalculateTrackScoresAsync(mapA.MapId, 0, 0, 1));
            first.Db.Aop.OnLogExecuting = null;
            Assert.Equal(2000u, (await first.Db.Queryable<PlayerTrackScoreEntity>().Where(x => x.MapId == mapA.MapId).FirstAsync()).Points);
            Assert.Equal(4000u, (await first.Db.Queryable<PlayerEntity>().Where(x => x.SteamId == playerValue).FirstAsync()).Points);

            // A wipe waits for an in-flight finish, then removes its run, best row
            // and checkpoints as one unit. Failures also roll the entire wipe back.
            await second.GetPlayerStageRecords(player, mapA.MapName);
            var metadata = new ReplayEntity
            {
                MapId = mapA.MapId, SteamId = unchecked((long)player.AsPrimitive()), RunId = best.RunId, Replay = "test.replay", CreatedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), UpdatedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            Assert.True(await first.SaveReplayMetadataAsync(metadata));
            Assert.True(await first.SaveReplayMetadataAsync(metadata));
            Assert.Equal(1, await first.Db.Queryable<ReplayEntity>().Where(x => x.MapId == mapA.MapId).CountAsync());
            ulong checkpointRunId = 0;
            using (var hold = new SqlPause(first, IsBestRunWrite))
            using (var attempted = new SqlSignal(second, IsMapLock))
            {
                var finish = Task.Run(() => first.AddPlayerStageRecord(player, mapA.MapName, new RecordRequest
                {
                    Stage = 1, Time = 50, Checkpoints = [new RecordRequest.CheckpointRecord { CheckpointIndex = 1, Time = 25 }],
                }));
                Task? wipe = null;
                try
                {
                    await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    wipe = Task.Run(() => second.RemoveMapRecords(mapA.MapName));
                    await attempted.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    await Task.Delay(100);
                    Assert.False(wipe.IsCompleted);
                }
                finally { hold.Release.Set(); checkpointRunId = (ulong)(await finish).Item2.Id; if (wipe is not null) await wipe; }
            }
            Assert.Equal(0, await first.Db.Queryable<RunEntity>().Where(x => x.MapId == mapA.MapId).CountAsync());
            Assert.Equal(0, await first.Db.Queryable<PlayerBestRunEntity>().Where(x => x.MapId == mapA.MapId).CountAsync());
            var deletedIds = saves.Select(x => (ulong)x.Item2.Id).Append(checkpointRunId).ToArray();
            Assert.Equal(0, await first.Db.Queryable<RunSegmentEntity>()
                .Where(x => deletedIds.Contains(x.RunId)).CountAsync());
            Assert.False(await first.SaveReplayMetadataAsync(new ReplayEntity
            {
                MapId = mapA.MapId, SteamId = unchecked((long)player.AsPrimitive()), RunId = best.RunId, Replay = "deleted-run.replay", CreatedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), UpdatedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }));
            Assert.Equal(0, await first.Db.Queryable<ReplayEntity>().Where(x => x.MapId == mapA.MapId).CountAsync());
            Assert.Equal(2000u, (await first.Db.Queryable<PlayerEntity>().Where(x => x.SteamId == playerValue).FirstAsync()).Points);
            // A queued recalc after deletion must not resurrect the removed scores.
            await first.RecalculateTrackScoresAsync(mapA.MapId, 0, 0, 1);
            Assert.Equal(0, await first.Db.Queryable<PlayerTrackScoreEntity>().Where(x => x.MapId == mapA.MapId).CountAsync());
            output.WriteLine($"{dialect}: rollback and finish/wipe ordering passed.");

            await CheckOutboxConcurrency(first, second, third, mapB.MapId);
            await CheckRunSubmissionConcurrency(first, second, mapB, mapC, playerValue, NewStore);
            await CheckBatching(first, tag, player.AsPrimitive() + 1000000000UL);
            await CheckPrimitiveMapping(first, mapB.MapId, playerValue);
            CheckLegacyReplayMigration(first, tag, playerValue);
            await CheckWarmFinish(first, mapC.MapName, player);
        }
        finally { foreach (var store in stores) store.Shutdown(); }
    }

    private async Task CheckOutboxConcurrency(StorageServiceImpl first,
                                              StorageServiceImpl second,
                                              StorageServiceImpl third,
                                              ulong mapId)
    {
        const int simultaneousInsertStyle = 71;
        var simultaneousAt = DateTime.UtcNow.AddMinutes(-1);
        await Task.WhenAll(
            first.EnqueueScoreRecalcAsync(mapId, simultaneousInsertStyle, 0, 1, simultaneousAt),
            second.EnqueueScoreRecalcAsync(mapId, simultaneousInsertStyle, 0, 1, simultaneousAt));
        var simultaneous = await first.Db.Queryable<ScoreRecalcOutboxEntity>()
                                      .Where(x => x.MapId == mapId && x.Style == simultaneousInsertStyle)
                                      .SingleAsync();
        Assert.Equal(2, simultaneous.RequestedGeneration);
        Assert.Equal(1, await first.Db.Queryable<ScoreRecalcOutboxEntity>()
                                     .Where(x => x.MapId == mapId && x.Style == simultaneousInsertStyle)
                                     .CountAsync());
        Assert.Equal(1, await first.ProcessScoreRecalcOutboxBatchAsync(
                         simultaneous.AvailableAtUtc, "simultaneous-insert-worker"));

        var mapName = await first.Db.Queryable<MapEntity>()
                                     .Where(x => x.MapId == mapId)
                                     .Select(x => x.File)
                                     .SingleAsync();
        var rollbackPlayer = new SteamID(76561198000000000UL + (ulong)Random.Shared.NextInt64(1000000001, 1999999999));
        await first.GetPlayerProfile(rollbackPlayer, "Outbox rollback");
        var segmentsBeforeRollback = await first.Db.Queryable<RunSegmentEntity>().CountAsync();

        first.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (IsOutboxInsert(sql))
            {
                throw new InvalidOperationException("Injected real-database Outbox insert failure");
            }
        };
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => first.AddPlayerRecord(
                rollbackPlayer,
                mapName,
                new RecordRequest
                {
                    Style = 72,
                    Time = 70,
                    Checkpoints = [new RecordRequest.CheckpointRecord { CheckpointIndex = 1, Time = 35 }],
                }));
        }
        finally
        {
            first.Db.Aop.OnLogExecuting = null;
        }

        Assert.Equal(0, await first.Db.Queryable<RunEntity>()
                                     .Where(x => x.MapId == mapId && x.Style == 72)
                                     .CountAsync());
        Assert.Equal(0, await first.Db.Queryable<PlayerBestRunEntity>()
                                     .Where(x => x.MapId == mapId && x.Style == 72)
                                     .CountAsync());
        Assert.Equal(segmentsBeforeRollback, await first.Db.Queryable<RunSegmentEntity>().CountAsync());
        Assert.Equal(0, await first.Db.Queryable<ScoreRecalcOutboxEntity>()
                                     .Where(x => x.MapId == mapId && x.Style == 72)
                                     .CountAsync());

        const int style = 73;
        const ushort track = 2;
        const int requestCount = 40;
        var firstRequestedAt = DateTime.UtcNow.AddMinutes(-2);

        await first.EnqueueScoreRecalcAsync(mapId, style, track, 1, firstRequestedAt);

        static async Task EnqueueRange(StorageServiceImpl store,
                                       ulong mapId,
                                       int style,
                                       ushort track,
                                       int start,
                                       int count,
                                       DateTime firstRequestedAt)
        {
            for (var index = start; index < start + count; index++)
            {
                await store.EnqueueScoreRecalcAsync(mapId,
                                                    style,
                                                    track,
                                                    1 + index / 100d,
                                                    firstRequestedAt.AddSeconds(index));
            }
        }

        await Task.WhenAll(EnqueueRange(first, mapId, style, track, 1, 20, firstRequestedAt),
                           EnqueueRange(second, mapId, style, track, 21, 19, firstRequestedAt));

        var merged = await first.Db.Queryable<ScoreRecalcOutboxEntity>()
                                   .Where(x => x.MapId == mapId && x.Style == style && x.Track == track)
                                   .SingleAsync();
        Assert.Equal(requestCount, merged.RequestedGeneration);
        Assert.Equal(0, merged.ProcessedGeneration);
        Assert.InRange(Math.Abs((merged.AvailableAtUtc - firstRequestedAt.AddSeconds(5)).TotalSeconds), 0, 1);

        var claims = await Task.WhenAll(
            first.ProcessScoreRecalcOutboxBatchAsync(firstRequestedAt.AddMinutes(1), "concurrent-worker-a"),
            second.ProcessScoreRecalcOutboxBatchAsync(firstRequestedAt.AddMinutes(1), "concurrent-worker-b"));
        Assert.Equal(1, claims.Sum());

        var initiallyCompleted = await first.Db.Queryable<ScoreRecalcOutboxEntity>()
                                                .Where(x => x.MapId == mapId && x.Style == style && x.Track == track)
                                                .SingleAsync();
        Assert.Equal(requestCount, initiallyCompleted.ProcessedGeneration);
        Assert.Null(initiallyCompleted.LeaseOwner);

        var nextRequestedAt = DateTime.UtcNow;
        await first.EnqueueScoreRecalcAsync(mapId, style, track, 2, nextRequestedAt);
        var freshGeneration = await first.Db.Queryable<ScoreRecalcOutboxEntity>()
                                             .Where(x => x.MapId == mapId && x.Style == style && x.Track == track)
                                             .SingleAsync();
        Assert.Equal(requestCount + 1, freshGeneration.RequestedGeneration);
        Assert.Equal(requestCount, freshGeneration.ProcessedGeneration);
        Assert.InRange(Math.Abs((freshGeneration.AvailableAtUtc - nextRequestedAt.AddSeconds(5)).TotalSeconds), 0, 1);
        using (var completionPause = new SqlPause(first, IsOutboxCompletionUpdate))
        {
            var processing = Task.Run(() => first.ProcessScoreRecalcOutboxBatchAsync(
                                          freshGeneration.AvailableAtUtc, "generation-worker"));
            try
            {
                // The recalculation holds no map lock, so a PB's enqueue commits while it runs;
                // its completion then keeps the newer generation pending.
                await completionPause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await second.EnqueueScoreRecalcAsync(mapId, style, track, 3, nextRequestedAt.AddSeconds(1))
                            .WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally
            {
                completionPause.Release.Set();
                await processing;
            }
        }

        var newerGeneration = await third.Db.Queryable<ScoreRecalcOutboxEntity>()
                                             .Where(x => x.MapId == mapId && x.Style == style && x.Track == track)
                                             .SingleAsync();
        Assert.Equal(requestCount + 2, newerGeneration.RequestedGeneration);
        Assert.Equal(requestCount + 1, newerGeneration.ProcessedGeneration);
        Assert.Null(newerGeneration.LeaseOwner);
        Assert.Equal(1, await third.ProcessScoreRecalcOutboxBatchAsync(
                         newerGeneration.AvailableAtUtc, "latest-generation-worker"));

        var finallyCompleted = await third.Db.Queryable<ScoreRecalcOutboxEntity>()
                                              .Where(x => x.MapId == mapId && x.Style == style && x.Track == track)
                                              .SingleAsync();
        Assert.Equal(finallyCompleted.RequestedGeneration, finallyCompleted.ProcessedGeneration);

        var leaseExpiryRequestedAt = DateTime.UtcNow;
        await first.EnqueueScoreRecalcAsync(mapId, style, track, 4, leaseExpiryRequestedAt);
        var leaseExpiryRequest = await first.Db.Queryable<ScoreRecalcOutboxEntity>()
                                                .Where(x => x.MapId == mapId && x.Style == style && x.Track == track)
                                                .SingleAsync();
        // Nothing holds the map, so the recovery owner completes the generation while the expired
        // owner is still running; the expired owner's completion then finds it processed and rolls back.
        using (var lateCompletionPause = new SqlPause(first, IsOutboxCompletionUpdate))
        {
            var lateOwner = Task.Run(() => first.ProcessScoreRecalcOutboxBatchAsync(
                                         leaseExpiryRequest.AvailableAtUtc, "expired-owner"));
            try
            {
                await lateCompletionPause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(1, await second.ProcessScoreRecalcOutboxBatchAsync(
                                    leaseExpiryRequest.AvailableAtUtc.AddMinutes(2), "recovery-owner")
                                .WaitAsync(TimeSpan.FromSeconds(15)));
            }
            finally
            {
                lateCompletionPause.Release.Set();
                await lateOwner;
            }
        }

        var recovered = await third.Db.Queryable<ScoreRecalcOutboxEntity>()
                                       .Where(x => x.MapId == mapId && x.Style == style && x.Track == track)
                                       .SingleAsync();
        Assert.Equal(recovered.RequestedGeneration, recovered.ProcessedGeneration);
        Assert.Null(recovered.LeaseOwner);
        Assert.Null(recovered.LeaseUntilUtc);

        // A worker can stall before its generation check, lose its lease, and resume only after a
        // newer generation has completed. The in-transaction generation fence must prevent
        // that old StyleFactor from becoming the final score state.
        var staleFactorRequestedAt = DateTime.UtcNow.AddMinutes(-1);
        await first.EnqueueScoreRecalcAsync(mapId, style: 0, track: 0, styleFactor: 1,
                                            nowUtc: staleFactorRequestedAt);
        var staleFactorRequest = await first.Db.Queryable<ScoreRecalcOutboxEntity>()
                                               .Where(x => x.MapId == mapId && x.Style == 0 && x.Track == 0)
                                               .SingleAsync();
        uint newerWorkerPoints = 0;
        using (var beforeMapLockPause = new SqlPause(first, IsGenerationCheck))
        {
            var staleWorker = Task.Run(() => first.ProcessScoreRecalcOutboxBatchAsync(
                                           staleFactorRequest.AvailableAtUtc, "stale-factor-owner"));
            try
            {
                await beforeMapLockPause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await second.EnqueueScoreRecalcAsync(mapId, style: 0, track: 0, styleFactor: 3,
                                                     nowUtc: staleFactorRequestedAt.AddSeconds(1));
                Assert.Equal(1, await second.ProcessScoreRecalcOutboxBatchAsync(
                                 staleFactorRequest.AvailableAtUtc.AddMinutes(2), "new-factor-owner"));
                newerWorkerPoints = await third.Db.Queryable<PlayerTrackScoreEntity>()
                                               .Where(x => x.MapId == mapId && x.Style == 0 && x.Track == 0)
                                               .Select(x => x.Points)
                                               .SingleAsync();
                Assert.True(newerWorkerPoints > 0);
            }
            finally
            {
                beforeMapLockPause.Release.Set();
                await staleWorker;
            }
        }

        Assert.Equal(newerWorkerPoints, await third.Db.Queryable<PlayerTrackScoreEntity>()
                                                   .Where(x => x.MapId == mapId && x.Style == 0 && x.Track == 0)
                                                   .Select(x => x.Points)
                                                   .SingleAsync());
        output.WriteLine($"{first.Db.CurrentConnectionConfig.DbType}: SQL Outbox concurrent merge, claim and generation handoff passed.");
    }

    private async Task CheckRunSubmissionConcurrency(StorageServiceImpl first,
                                                     StorageServiceImpl second,
                                                     MapProfile mapB,
                                                     MapProfile mapC,
                                                     long steamId,
                                                     Func<StorageServiceImpl> newStore)
    {
        const int sameIdStyle = 15;
        const ushort sameIdTrack = 31;
        var sameId = Guid.NewGuid();
        var sameIdText = sameId.ToString("N");
        var command = CreateSubmissionCommand(sameId, steamId, mapB.MapName,
                                              sameIdStyle, sameIdTrack, 70_000_000);

        // Eight real SQL scopes issue one hundred calls, retaining meaningful cross-process
        // contention without opening one hundred database connections at once.
        var workers = Enumerable.Range(0, 8).Select(_ => newStore()).ToArray();
        try
        {
            var batches = await Task.WhenAll(workers.Select((worker, workerIndex) => Task.Run(async () =>
            {
                var outcomes = new List<TimerBackendRunSubmissionResult>();
                for (var index = workerIndex; index < 100; index += workers.Length)
                {
                    outcomes.Add(await worker.SubmitBackendRunAsync(command));
                }

                return outcomes;
            })));
            var outcomes = batches.SelectMany(x => x).ToArray();
            Assert.Equal(1, outcomes.Count(x => x.Disposition == TimerBackendSubmissionDisposition.Accepted));
            Assert.Equal(99, outcomes.Count(x => x.Disposition == TimerBackendSubmissionDisposition.AlreadyApplied));

            var accepted = Assert.Single(outcomes, x => x.Disposition == TimerBackendSubmissionDisposition.Accepted);
            Assert.Equal(1, await first.Db.Queryable<RunSubmissionEntity>()
                                       .Where(x => x.SubmissionId == sameIdText)
                                       .CountAsync());
            Assert.Equal(1, await first.Db.Queryable<RunEntity>()
                                       .Where(x => x.MapId == mapB.MapId && x.SteamId == steamId
                                                   && x.Style == sameIdStyle && x.Track == sameIdTrack)
                                       .CountAsync());
            Assert.Equal(2, await first.Db.Queryable<RunSegmentEntity>()
                                       .Where(x => x.RunId == accepted.RunId)
                                       .CountAsync());
            var outbox = await first.Db.Queryable<ScoreRecalcOutboxEntity>()
                                    .Where(x => x.MapId == mapB.MapId && x.Style == sameIdStyle && x.Track == sameIdTrack)
                                    .SingleAsync();
            Assert.Equal(1, outbox.RequestedGeneration);

            // Different-map payloads cannot both claim one global submission key. The
            // inbox unique index decides the winner; its loser must roll back every dependent
            // row and surface a permanent payload conflict rather than a transient SQL error.
            const int crossMapStyle = 14;
            const ushort crossMapTrack = 30;
            var crossMapId = Guid.NewGuid();
            var crossMapIdText = crossMapId.ToString("N");
            var left = CaptureSubmissionAsync(first.SubmitBackendRunAsync(
                CreateSubmissionCommand(crossMapId, steamId, mapB.MapName,
                                        crossMapStyle, crossMapTrack, 69_000_000)));
            var right = CaptureSubmissionAsync(second.SubmitBackendRunAsync(
                CreateSubmissionCommand(crossMapId, steamId, mapC.MapName,
                                        crossMapStyle, crossMapTrack, 68_000_000)));
            var crossMapOutcomes = await Task.WhenAll(left, right);
            var crossMapAccepted = Assert.Single(crossMapOutcomes, x => x.Result is not null).Result!;
            Assert.Single(crossMapOutcomes, x => x.Error is TimerBackendSubmissionConflictException);

            Assert.Equal(1, await first.Db.Queryable<RunSubmissionEntity>()
                                       .Where(x => x.SubmissionId == crossMapIdText)
                                       .CountAsync());
            Assert.Equal(1, await first.Db.Queryable<RunEntity>()
                                       .Where(x => (x.MapId == mapB.MapId || x.MapId == mapC.MapId)
                                                   && x.SteamId == steamId && x.Style == crossMapStyle
                                                   && x.Track == crossMapTrack)
                                       .CountAsync());
            Assert.Equal(2, await first.Db.Queryable<RunSegmentEntity>()
                                       .Where(x => x.RunId == crossMapAccepted.RunId)
                                       .CountAsync());
            var crossMapOutboxes = await first.Db.Queryable<ScoreRecalcOutboxEntity>()
                                              .Where(x => (x.MapId == mapB.MapId || x.MapId == mapC.MapId)
                                                          && x.Style == crossMapStyle && x.Track == crossMapTrack)
                                              .ToListAsync();
            Assert.Single(crossMapOutboxes);
            Assert.Equal(1, crossMapOutboxes[0].RequestedGeneration);
        }
        finally
        {
            // NewStore registered every worker in RunSuite's owning collection; its outer
            // finally disposes all scopes exactly once, including failure paths here.
        }

        output.WriteLine($"{first.Db.CurrentConnectionConfig.DbType}: 100 same-id submissions and cross-map inbox conflict passed.");
    }

    private static async Task<(TimerBackendRunSubmissionResult? Result, Exception? Error)> CaptureSubmissionAsync(
        Task<TimerBackendRunSubmissionResult> submission)
    {
        try
        {
            return (await submission, null);
        }
        catch (Exception exception)
        {
            return (null, exception);
        }
    }

    private static TimerBackendRunSubmissionCommand CreateSubmissionCommand(Guid submissionId,
                                                                              long steamId,
                                                                              string mapName,
                                                                              int style,
                                                                              ushort track,
                                                                              long timeMicros)
        => new ()
        {
            SubmissionId = submissionId,
            SteamId = steamId,
            MapName = mapName,
            Kind = TimerBackendRunKind.Main,
            Style = style,
            Track = track,
            TimeMicros = timeMicros,
            Jumps = 8,
            Strafes = 12,
            Sync = 98,
            Motion = new TimerBackendMotion { VelocityStartX = 1, VelocityEndZ = 2, VelocityAvgY = 3 },
            Checkpoints =
            [
                new TimerBackendSubmissionCheckpoint
                {
                    CheckpointIndex = 1, TimeMicros = timeMicros / 2, Sync = 97,
                    Motion = new TimerBackendMotion { VelocityStartY = 4, VelocityMaxZ = 5 },
                },
                new TimerBackendSubmissionCheckpoint
                {
                    CheckpointIndex = 2, TimeMicros = timeMicros - 1, Sync = 96,
                    Motion = new TimerBackendMotion { VelocityEndX = 6, VelocityAvgZ = 7 },
                },
            ],
            FinishedAtUtc = new DateTime(2026, 9, 15, 1, 2, 3, DateTimeKind.Utc),
            RulesetVersion = 1,
            StyleFactor = 1,
        };

    private async Task CheckBatching(StorageServiceImpl store, string tag, ulong playerBase)
    {
        const int count = 2000;
        var map = await store.GetMapInfo($"surf_batch_{tag}");
        foreach (var batch in Enumerable.Range(0, count).Chunk(500))
        {
            await store.Db.Insertable(batch.Select(i => new PlayerEntity { SteamId = checked((long)(playerBase + (ulong)i)), Name = "Batch test", UpdatedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }).ToArray()).ExecuteCommandAsync();
            await store.Db.Insertable(batch.Select(i => new RunEntity
            {
                MapId = map.MapId,
                SteamId = checked((long)(playerBase + (ulong)i)),
                Time = 80 + i,
                DateUnixTimeMilliseconds = StorageServiceImpl.ToUnixTimeMilliseconds(DateTime.UtcNow),
            }).ToArray()).ExecuteCommandAsync();
        }
        var commands = 0;
        var scoreWrites = 0;
        store.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            commands++;
            var normalized = sql.TrimStart().Replace("`", "").Replace("\"", "");
            if (normalized.StartsWith("INSERT INTO surf_player_track_scores", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("UPDATE surf_player_track_scores", StringComparison.OrdinalIgnoreCase)) scoreWrites++;
        };
        var watch = Stopwatch.StartNew();
        await store.RecalculateTrackScoresAsync(map.MapId, 0, 0, 1);
        output.WriteLine($"{store.Db.CurrentConnectionConfig.DbType}: {count} players cold seed + recalc: {watch.ElapsedMilliseconds} ms, {commands} SQL commands.");
        Assert.True(commands < 100, $"Expected bounded batches, got {commands} SQL commands.");
        await store.Db.Updateable<MapEntity>().SetColumns(x => x.BasePot == 2000).Where(x => x.MapId == map.MapId).ExecuteCommandAsync();
        commands = 0;
        watch.Restart();
        await store.RecalculateTrackScoresAsync(map.MapId, 0, 0, 1);
        output.WriteLine($"{store.Db.CurrentConnectionConfig.DbType}: changed recalc: {watch.ElapsedMilliseconds} ms, {commands} SQL commands.");
        Assert.True(commands < 40);
        var winner = checked((long)playerBase);
        Assert.Equal(2000u, (await store.Db.Queryable<PlayerEntity>().Where(x => x.SteamId == winner).FirstAsync()).Points);
        commands = 0;
        scoreWrites = 0;
        watch.Restart();
        await store.RecalculateTrackScoresAsync(map.MapId, 0, 0, 1);
        output.WriteLine($"{store.Db.CurrentConnectionConfig.DbType}: unchanged recalc: {watch.ElapsedMilliseconds} ms, {commands} SQL commands, {scoreWrites} track-score writes.");
        Assert.Equal(0, scoreWrites);
        Assert.True(commands < 40);
        store.Db.Aop.OnLogExecuting = null;
    }

    private static async Task CheckPrimitiveMapping(StorageServiceImpl store, ulong mapId, long player)
    {
        var row = new PlayerTrackScoreEntity { SteamId = player, MapId = mapId, Style = 99, Track = 1, Points = 5, UpdatedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        await store.Db.Insertable(row).ExecuteCommandAsync();
        row.Points = 10;
        // This exact one-element batch lost the SteamId parameter with the custom
        // ISugarDataConverter on both 5.1.4.211 and 5.1.4.220.
        await store.Db.Updateable(new[] { row }).WhereColumns(x => new { x.SteamId, x.MapId, x.Style, x.Track })
            .UpdateColumns(x => x.Points).ExecuteCommandAsync();
        Assert.Equal(10u, await store.Db.Queryable<PlayerTrackScoreEntity>()
            .Where(x => x.MapId == mapId && x.Style == 99 && x.SteamId == player).Select(x => x.Points).SingleAsync());
        Assert.Equal(player, await store.Db.Queryable<PlayerTrackScoreEntity>()
            .Where(x => x.MapId == mapId && x.Style == 99).Select(x => x.SteamId).SingleAsync());
        row.Points = 15;
        await store.Db.Storageable(row).WhereColumns(x => new { x.SteamId, x.MapId, x.Style, x.Track }).ExecuteCommandAsync();
        Assert.Equal(15u, await store.Db.Queryable<PlayerTrackScoreEntity>()
            .Where(x => x.MapId == mapId && x.Style == 99).Select(x => x.Points).SingleAsync());
    }

    private static void CheckLegacyReplayMigration(StorageServiceImpl store, string tag, long player)
    {
        var table = $"replay_migration_{tag}";
        store.Db.CodeFirst.As<LegacyReplayRow>(table).InitTables<LegacyReplayRow>();
        store.Db.Insertable(new LegacyReplayRow { SteamId = player.ToString(), MapId = 1, RunId = 2 }).AS(table).ExecuteCommand();
        store.MigrateReplaySteamIdColumn(table);
        var row = store.Db.Queryable<MigratedReplayRow>().AS(table).Single();
        Assert.Equal(player, row.SteamId);
        Assert.Equal(1UL, row.MapId);
        Assert.Equal(2UL, row.RunId);
        Assert.ThrowsAny<Exception>(() => store.Db.Insertable(row).AS(table).ExecuteCommand());
        // A second startup must be a no-op.
        store.MigrateReplaySteamIdColumn(table);
        Assert.Equal(1, store.Db.Queryable<MigratedReplayRow>().AS(table).Count());

        if (store.Db.CurrentConnectionConfig.DbType == DbType.PostgreSQL)
        {
            var invalidTable = $"replay_invalid_{tag}";
            store.Db.CodeFirst.As<LegacyReplayRow>(invalidTable).InitTables<LegacyReplayRow>();
            store.Db.Insertable(new LegacyReplayRow { SteamId = "invalid", MapId = 1, RunId = 2 }).AS(invalidTable).ExecuteCommand();
            Assert.ThrowsAny<Exception>(() => store.MigrateReplaySteamIdColumn(invalidTable));
            Assert.False(store.Db.DbMaintenance.IsAnyColumn(invalidTable, "steamid_bigint_migration", false));
            Assert.Equal("invalid", store.Db.Queryable<LegacyReplayRow>().AS(invalidTable).Single().SteamId);
        }
    }

    private async Task CheckWarmFinish(StorageServiceImpl store, string map, SteamID player)
    {
        await store.AddPlayerStageRecord(player, map, new RecordRequest { Stage = 1, Time = 80 });
        var commands = 0;
        var bestWrites = 0;
        store.Db.Aop.OnLogExecuting = (sql, _) => { commands++; if (IsBestRunWrite(sql)) bestWrites++; };
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
            await store.AddPlayerStageRecord(player, map, new RecordRequest { Stage = 1, Time = 90 });
        store.Db.Aop.OnLogExecuting = null;
        Assert.Equal(0, bestWrites);
        Assert.Equal(60, commands); // map lock, current bests, run insert; no existence probe
        output.WriteLine($"{store.Db.CurrentConnectionConfig.DbType}: 20 warm slower finishes: {watch.ElapsedMilliseconds} ms, {commands} SQL commands, no best-row writes.");
    }

    // The legacy table once its SteamId is migrated; its dates stay the old timestamps.
    private sealed class MigratedReplayRow
    {
        [SugarColumn(IsPrimaryKey = true, ColumnDataType = "bigint")] public long SteamId { get; set; }
        [SugarColumn(IsPrimaryKey = true)] public ulong MapId { get; set; }
        [SugarColumn(IsPrimaryKey = true)] public ulong RunId { get; set; }
        public string Replay { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    private sealed class LegacyReplayRow
    {
        [SugarColumn(IsPrimaryKey = true, Length = 32)] public string SteamId { get; set; } = "";
        [SugarColumn(IsPrimaryKey = true)] public ulong MapId { get; set; }
        [SugarColumn(IsPrimaryKey = true)] public ulong RunId { get; set; }
        public string Replay { get; set; } = "migration.replay";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    private static bool IsMapLock(string sql) => sql.Contains("surf_maps") && sql.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase);
    private static bool IsGenerationCheck(string sql) => Regex.IsMatch(sql, @"^\s*SELECT\s+1\s+FROM", RegexOptions.IgnoreCase)
                                                        && sql.Contains("surf_score_recalc_outbox", StringComparison.OrdinalIgnoreCase);
    private static bool IsPlayerUpdate(string sql) => sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) && sql.Contains("surf_players");
    private static bool IsBestRunWrite(string sql) => sql.Contains("surf_player_best_runs")
        && (sql.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) || sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase));

    private static bool IsOutboxInsert(string sql)
        => sql.Contains("surf_score_recalc_outbox", StringComparison.OrdinalIgnoreCase)
           && sql.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase);

    private static bool IsOutboxCompletionUpdate(string sql)
    {
        if (!sql.Contains("surf_score_recalc_outbox", StringComparison.OrdinalIgnoreCase)
            || !sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var whereIndex = sql.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
        var processedGenerationIndex = sql.IndexOf("ProcessedGeneration", StringComparison.OrdinalIgnoreCase);
        return processedGenerationIndex >= 0
               && (whereIndex < 0 || processedGenerationIndex < whereIndex);
    }

    private sealed class SqlPause : IDisposable
    {
        private readonly StorageServiceImpl _store;
        private int _used;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public SqlPause(StorageServiceImpl store, Func<string, bool> predicate)
        {
            _store = store;
            store.Db.Aop.OnLogExecuting = (sql, _) =>
            {
                if (!predicate(sql) || Interlocked.Exchange(ref _used, 1) != 0) return;
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("SQL barrier was not released.");
            };
        }
        public void Dispose() { Release.Set(); _store.Db.Aop.OnLogExecuting = null; Release.Dispose(); }
    }

    private sealed class SqlSignal : IDisposable
    {
        private readonly StorageServiceImpl _store;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SqlSignal(StorageServiceImpl store, Func<string, bool> predicate)
        {
            _store = store;
            store.Db.Aop.OnLogExecuting = (sql, _) => { if (predicate(sql)) Entered.TrySetResult(); };
        }
        public void Dispose() => _store.Db.Aop.OnLogExecuting = null;
    }

    private sealed class DatabaseFactAttribute : FactAttribute
    {
        public DatabaseFactAttribute(string environment)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environment))) Skip = $"Set {environment} to a disposable test database.";
        }
    }
}

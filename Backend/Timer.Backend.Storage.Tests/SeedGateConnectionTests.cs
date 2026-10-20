using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using SqlSugar;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Storage.Tests;

// Requests queued behind a slow best-run seed must not each pin a pooled connection: a cold map hit by a full
// server would otherwise exhaust the pool and fail unrelated requests with connection timeouts.
public sealed class SeedGateConnectionTests
{
    [DatabaseFact("TIMER_TEST_MYSQL")]
    public async Task WaitersBehindASlowSeedDoNotExhaustTheConnectionPool()
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = Environment.GetEnvironmentVariable("TIMER_TEST_MYSQL")! };
        var host = Convert.ToString(builder["Server"]);
        Assert.True(host is "127.0.0.1" or "localhost" or "::1"
                    && Convert.ToString(builder["Database"])!.Contains("test", StringComparison.OrdinalIgnoreCase));
        builder["Pooling"] = "True";
        builder["Maximum Pool Size"] = "4";
        builder["Connection Timeout"] = "2";

        var store = new StorageServiceImpl(DbType.MySql, builder.ConnectionString, NullLogger<StorageServiceImpl>.Instance, false);
        try
        {
            store.Db.DbMaintenance.CreateDatabase();
            store.Init(startScoreRecalcWorker: false);
            var cold = await store.GetMapInfo($"surf_seed_gate_{Guid.NewGuid():N}");
            var other = $"surf_seed_gate_other_{Guid.NewGuid():N}";
            await store.GetMapInfo(other);
            await store.Db.Insertable(new RunEntity
            {
                MapId = cold.MapId, SteamId = 76561198000000001, RunType = RunType.Main, Time = 60,
                DateUnixTimeMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }).ExecuteCommandAsync();

            // Pause the seed (the only query ranking runs per player) while it holds the gate, until the
            // unrelated request below has had its chance at the pool.
            var seeding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            store.Db.CurrentConnectionConfig.AopEvents.OnLogExecuting = (sql, _) =>
            {
                if (sql.Contains("PARTITION BY", StringComparison.OrdinalIgnoreCase) && seeding.TrySetResult())
                {
                    release.Wait(TimeSpan.FromSeconds(15));
                }
            };

            var readers = Enumerable.Range(0, 12)
                                    .Select(_ => Task.Run(() => store.RunOperationAsync(() => store.GetMapRecords(cold.MapName), CancellationToken.None)))
                                    .ToArray();
            try
            {
                await seeding.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Task.Delay(300); // the other readers reach the gate behind the seed

                // Without releasing waiters' connections, 11 of them would hold all 4 and this times out (2 s).
                var unrelated = await store.RunOperationAsync(() => store.GetMapInfo(other), CancellationToken.None);
                Assert.Equal(other, unrelated.MapName);
            }
            finally
            {
                release.Set();
            }

            foreach (var records in await Task.WhenAll(readers))
            {
                Assert.Single(records);
            }
        }
        finally
        {
            store.Db.CurrentConnectionConfig.AopEvents.OnLogExecuting = null;
            store.Shutdown();
        }
    }

    private sealed class DatabaseFactAttribute : FactAttribute
    {
        public DatabaseFactAttribute(string variable)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
                Skip = $"Set {variable} to a disposable loopback test database.";
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using MagicOnion.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Backend.Contracts;
using Source2Surf.Timer.Backend.Rpc.Contracts;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.Backend.Configuration;
using Timer.Backend.Endpoints;
using Timer.Backend.Infrastructure;
using Timer.Backend.WriteApi;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Tests;

public sealed class BackendResilienceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AdministrativeOperationsRespectCancellationAndRecover(int operationIndex)
    {
        using var fixture = new Fixture();
        fixture.Owner.Start(false, false);
        var policy = new Dictionary<int, double> { [0] = 1 };
        Func<CancellationToken, Task<TimerBackendScoreAdministrationResult>> operation = operationIndex switch
        {
            0 => token => fixture.Owner.SetMapTierAndRequeueScoresAsync("surf_admin_cancel", 2, policy, token),
            1 => token => fixture.Owner.RequeueMapScorePolicyAsync("surf_admin_cancel", policy, token),
            _ => token => fixture.Owner.RequeueAllScorePoliciesAsync(policy, token),
        };
        var commands = 0;
        fixture.Store.Db.Aop.OnLogExecuting = (_, _) => Interlocked.Increment(ref commands);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation(cancellation.Token));
        Assert.Equal(0, commands);
        await operation(CancellationToken.None);
        Assert.True(commands > 0);
    }

    [Fact]
    public async Task DisposalAllowsAnActiveAdministrativeTransactionToFinish()
    {
        using var fixture = new Fixture();
        var map = await fixture.Store.GetMapInfo("surf_admin_dispose");
        fixture.Owner.Start(false, false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        fixture.Store.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (!sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                || !sql.Contains("surf_maps", StringComparison.OrdinalIgnoreCase)) return;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Admin transaction was not released.");
        };
        var request = Task.Run(() => fixture.Owner.SetMapTierAndRequeueScoresAsync(
            map.MapName, 2, new Dictionary<int, double> { [0] = 1 }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Owner.Dispose();
            Assert.False(request.IsCompleted);
        }
        finally { release.Set(); }
        Assert.Equal<byte?>(2, (await request.WaitAsync(TimeSpan.FromSeconds(5))).CurrentTier);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Owner.GetAllMapNamesAsync());
    }

    [Fact]
    public async Task RepeatedNewGenerationsCannotHideUnresolvedWorkerFailures()
    {
        using var fixture = new Fixture(worker: true);
        fixture.Owner.Start(false, false);
        await fixture.Owner.StopWorkerAsync(default);
        var map = await fixture.Store.GetMapInfo("surf_busy_failure");
        var pendingSince = DateTime.UtcNow.AddMinutes(-20);
        for (var index = 0; index < 10; index++)
        {
            var requestedAt = pendingSince.AddMinutes(index * 2);
            await fixture.Store.EnqueueScoreRecalcAsync(map.MapId, 0, 0, 1, requestedAt);
            fixture.Store.Db.Aop.OnLogExecuting = (sql, _) =>
            {
                if (sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                    && sql.Contains("surf_player_track_scores", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Persistent score failure");
            };
            try { await fixture.Store.ProcessScoreRecalcOutboxBatchAsync(requestedAt.AddSeconds(5), $"busy-failure-{index}"); }
            finally { fixture.Store.Db.Aop.OnLogExecuting = null; }
        }
        // A fresh enqueue resets the delivery budget and brings retry close to now.
        await fixture.Store.EnqueueScoreRecalcAsync(map.MapId, 0, 0, 1, DateTime.UtcNow);
        var health = await fixture.Owner.GetWorkerHealthAsync();
        Assert.Equal(1, health.PendingCount);
        Assert.Equal(0, health.DeadLetterCount);
        Assert.InRange(Math.Abs((health.OldestPendingSinceUtc!.Value - pendingSince).TotalSeconds), 0, 1);
        await using var app = await StartAppAsync(fixture.Owner, timeout: TimeSpan.FromSeconds(3));
        using var client = new HttpClient { BaseAddress = Address(app) };
        using var response = await client.GetAsync("/health/worker");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("\"status\":\"degraded\"", await response.Content.ReadAsStringAsync());

        var pending = await fixture.Store.Db.Queryable<ScoreRecalcOutboxEntity>()
            .Where(x => x.MapId == map.MapId).SingleAsync();
        await fixture.Store.ProcessScoreRecalcOutboxBatchAsync(pending.AvailableAtUtc, "recovered");
        Assert.Null((await fixture.Owner.GetWorkerHealthAsync()).OldestPendingSinceUtc);
        var next = DateTime.UtcNow.AddMinutes(1);
        await fixture.Store.EnqueueScoreRecalcAsync(map.MapId, 0, 0, 1, next);
        Assert.InRange(Math.Abs(((await fixture.Owner.GetWorkerHealthAsync()).OldestPendingSinceUtc!.Value - next).TotalSeconds), 0, 1);
    }

    [Fact]
    public async Task DedicatedWorkerStartupRecreatesAMissingOutbox()
    {
        using var fixture = new Fixture(worker: true);
        fixture.Store.Db.DbMaintenance.DropTable<ScoreRecalcOutboxEntity>();
        var service = new TimerBackendStorageHostedService(
            fixture.Owner, new TimerBackendOptions("postgresql", "unused", false, true),
            WriteOptions(false), NullLogger<TimerBackendStorageHostedService>.Instance);

        await service.StartAsync(default);

        Assert.True(fixture.Store.Db.DbMaintenance.IsAnyTable("surf_score_recalc_outbox", false));
        await fixture.Owner.CheckReadyAsync();
        await service.StopAsync(default);
    }

    [Fact]
    public async Task DedicatedWorkerReadinessContinuesToRequireOutboxAfterStartup()
    {
        using var fixture = new Fixture(worker: true);
        fixture.Owner.Start(false, false);
        await fixture.Owner.StopWorkerAsync(default);
        fixture.Store.Db.DbMaintenance.DropTable<ScoreRecalcOutboxEntity>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Owner.CheckReadyAsync());
        Assert.Contains("surf_score_recalc_outbox", exception.Message);
    }

    [Fact]
    public async Task PreCancelledRequestExecutesNoSqlAndDoesNotCancelNextRequest()
    {
        using var fixture = new Fixture();
        fixture.Owner.Start(false, false);
        var commands = 0;
        fixture.Store.Db.Aop.OnLogExecuting = (_, _) => Interlocked.Increment(ref commands);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Owner.GetAllMapNamesAsync(cancellation.Token));
        Assert.Equal(0, commands);
        await fixture.Owner.GetAllMapNamesAsync();
        Assert.True(commands > 0);
    }

    [Fact]
    public async Task DisposalRetainsStorageUntilActiveRequestFinished()
    {
        using var fixture = new Fixture();
        fixture.Owner.Start(false, false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        fixture.Store.Db.Aop.OnLogExecuting = (_, _) =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test did not release query.");
        };
        var request = Task.Run(() => fixture.Owner.GetAllMapNamesAsync());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            fixture.Owner.Dispose();
            Assert.False(request.IsCompleted);
        }
        finally { release.Set(); }

        await request.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Owner.GetAllMapNamesAsync());
    }

    [Fact]
    public async Task RestTimeoutCancelsSqlAndReturns504ThenRecovers()
    {
        using var fixture = new Fixture();
        fixture.Owner.Start(false, false);
        var delaySql = true;
        await using var app = await StartAppAsync(fixture.Owner, onRequest: () =>
            fixture.Store.Db.Aop.OnLogExecuting = delaySql ? (_, _) => Thread.Sleep(200) : null);
        using var client = new HttpClient { BaseAddress = Address(app) };

        try
        {
            using var response = await client.GetAsync("/api/v1/maps").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
            Assert.Contains("request_timeout", await response.Content.ReadAsStringAsync());
        }
        finally { delaySql = false; }

        using var recovered = await client.GetAsync("/api/v1/maps");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task RpcServerBudgetCancelsSqlAndReturnsDeadlineExceeded()
    {
        using var fixture = new Fixture();
        fixture.Owner.Start(false, false, requireWriteSchema: true);
        await using var app = await StartAppAsync(fixture.Owner, http2: true, onRequest: () =>
            fixture.Store.Db.Aop.OnLogExecuting = (_, _) => Thread.Sleep(200));
        using var channel = GrpcChannel.ForAddress(Address(app));
        var client = MagicOnionClient.Create<ITimerWriteServiceV1>(channel);

        try
        {
            var exception = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.EnsurePlayerProfileAsync(new EnsurePlayerProfileRequest
                {
                    SteamId = 76561198000000001,
                    Name = "Timeout test",
                }));
            Assert.Equal(StatusCode.DeadlineExceeded, exception.StatusCode);
        }
        finally { fixture.Store.Db.Aop.OnLogExecuting = null; }

        Assert.Equal(0, await fixture.Store.Db.Queryable<PlayerEntity>().CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WorkerHealthExposesPersistedDeadLettersAs503()
    {
        using var fixture = new Fixture(worker: true);
        fixture.Owner.Start(false, false);
        var now = DateTime.UtcNow;
        await fixture.Store.Db.Insertable(new ScoreRecalcOutboxEntity
        {
            MapId = 999, RequestedGeneration = 1, StyleFactor = 1,
            AvailableAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now, DeadLetteredAtUtc = now,
        }).ExecuteCommandAsync();
        await using var app = await StartAppAsync(fixture.Owner, timeout: TimeSpan.FromSeconds(3));
        using var client = new HttpClient { BaseAddress = Address(app) };

        using var worker = await client.GetAsync("/health/worker");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, worker.StatusCode);
        Assert.Contains("\"deadLetterCount\":1", await worker.Content.ReadAsStringAsync());
        using var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task LeaderboardOutputCacheServesWarmResponsesAndLeavesOtherReadsUncached()
    {
        using var fixture = new Fixture();
        var map = await fixture.Store.GetMapInfo($"surf_cache_warm_{Guid.NewGuid():N}");
        var player = new SteamID(76561198000000001UL);
        await fixture.Store.AddPlayerRecord(player, map.MapName, new RecordRequest { Time = 80 });
        await fixture.Store.AddPlayerRecord(new SteamID(76561198000000002UL), map.MapName,
            new RecordRequest { Time = 90 });
        for (var index = 2; index < 8; index++)
        {
            await fixture.Store.AddPlayerRecord(
                new SteamID(76561198000000000UL + (ulong)index + 1),
                map.MapName, new RecordRequest { Time = 90 + index });
        }
        fixture.Owner.Start(false, false);
        await using var app = await StartAppAsync(fixture.Owner, timeout: TimeSpan.FromSeconds(5));
        using var client = new HttpClient { BaseAddress = Address(app) };
        var path = $"/api/v1/maps/{map.MapName}/leaderboard?limit=8";

        using var initial = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        var initialBody = await initial.Content.ReadAsStringAsync();
        var entityTag = initial.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrWhiteSpace(entityTag));
        using var compressedClient = new HttpClient { BaseAddress = Address(app) };
        using var compressedRequest = new HttpRequestMessage(HttpMethod.Get, path);
        compressedRequest.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        using var compressed = await compressedClient.SendAsync(compressedRequest);
        Assert.Equal(HttpStatusCode.OK, compressed.StatusCode);
        Assert.Equal(entityTag, compressed.Headers.ETag?.Tag);
        Assert.Equal("gzip", compressed.Content.Headers.ContentEncoding.Single());
        await using var compressedStream = await compressed.Content.ReadAsStreamAsync();
        using var gzip = new GZipStream(compressedStream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        Assert.Equal(initialBody, await reader.ReadToEndAsync());

        fixture.Store.Db.DbMaintenance.DropTable<RunEntity>();
        using var warm = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
        Assert.Equal(initialBody, await warm.Content.ReadAsStringAsync());

        // Parameters the endpoint ignores must not create a separate (uncached) entry.
        using var junkQuery = await client.GetAsync($"{path}&cachebuster={Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.OK, junkQuery.StatusCode);
        Assert.Equal(initialBody, await junkQuery.Content.ReadAsStringAsync());

        using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
        conditional.Headers.TryAddWithoutValidation("If-None-Match", entityTag);
        using var notModified = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Empty(await notModified.Content.ReadAsByteArrayAsync());

        using var otherLimit = await client.GetAsync(
            $"/api/v1/maps/{map.MapName}/leaderboard?limit=1");
        Assert.Equal(HttpStatusCode.InternalServerError, otherLimit.StatusCode);

        using var personal = await client.GetAsync(
            $"/api/v1/players/{player.AsPrimitive()}/maps/{map.MapName}/records");
        Assert.Equal(HttpStatusCode.InternalServerError, personal.StatusCode);
    }

    [Fact]
    public async Task LeaderboardCacheCannotBeBypassedOrMultipliedByClientInput()
    {
        using var fixture = new Fixture();
        var map = await fixture.Store.GetMapInfo($"surf_cache_bypass_{Guid.NewGuid():N}");
        await fixture.Store.AddPlayerRecord(new SteamID(76561198000000011UL), map.MapName, new RecordRequest { Time = 80 });
        fixture.Owner.Start(false, false);
        await using var app = await StartAppAsync(fixture.Owner, timeout: TimeSpan.FromSeconds(5));
        using var client = new HttpClient { BaseAddress = Address(app) };
        var path = $"/api/v1/maps/{map.MapName}/leaderboard?limit=5";

        using var initial = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        var body = await initial.Content.ReadAsStringAsync();
        using var gzipWarm = new HttpRequestMessage(HttpMethod.Get, path);
        gzipWarm.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        using var gzipInitial = await client.SendAsync(gzipWarm);
        Assert.Equal("gzip", gzipInitial.Content.Headers.ContentEncoding.Single());

        // From here on every database read fails: only cache hits can succeed.
        fixture.Store.Db.DbMaintenance.DropTable<RunEntity>();

        using var withAuthorization = new HttpRequestMessage(HttpMethod.Get, path);
        withAuthorization.Headers.TryAddWithoutValidation("Authorization", "x");
        using var authorized = await client.SendAsync(withAuthorization);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        Assert.Equal(body, await authorized.Content.ReadAsStringAsync());

        using var otherHost = new HttpRequestMessage(HttpMethod.Get, path);
        otherHost.Headers.Host = $"cache-buster-{Guid.NewGuid():N}.example";
        using var hosted = await client.SendAsync(otherHost);
        Assert.Equal(HttpStatusCode.OK, hosted.StatusCode);

        // The compressed entry is served as stored, not recompressed from the identity one.
        using var gzipAgain = new HttpRequestMessage(HttpMethod.Get, path);
        gzipAgain.Headers.TryAddWithoutValidation("Accept-Encoding", "deflate, gzip;q=0.8");
        using var gzipHit = await client.SendAsync(gzipAgain);
        Assert.Equal(HttpStatusCode.OK, gzipHit.StatusCode);
        Assert.Equal("gzip", gzipHit.Content.Headers.ContentEncoding.Single());

        // Other spellings of the same values are refused before any database work instead of becoming new entries.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/v1/maps/{map.MapName}/leaderboard?limit=05")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/v1/maps/{map.MapName}/leaderboard?limit=5&style=00")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/v1/maps/%20{map.MapName}/leaderboard?limit=5")).StatusCode);
    }

    [Fact]
    public async Task DatabaseReadsBeyondTheConcurrencyLimitAreRejected()
    {
        using var fixture = new Fixture();
        fixture.Owner.Start(false, false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        // Only the first request's SQL blocks, holding the single permit until released.
        fixture.Store.Db.Aop.OnLogExecuting = (_, _) =>
        {
            if (entered.TrySetResult())
            {
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };
        await using var app = await StartAppAsync(fixture.Owner, timeout: TimeSpan.FromSeconds(10),
            runtime: new TimerBackendRuntimeOptions { ReadConcurrencyLimit = 1, ReadQueueLimit = 0 });
        using var client = new HttpClient { BaseAddress = Address(app) };

        var first = client.GetAsync("/api/v1/maps");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var second = await client.GetAsync("/api/v1/maps");
        release.Set();
        using var firstResponse = await first;

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Contains("too_many_requests", await second.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false, HttpStatusCode.TooManyRequests)] // forwarded headers ignored: one loopback client
    [InlineData(true, HttpStatusCode.OK)]               // trusted proxy: two different clients
    public async Task ClientRateLimitUsesForwardedAddressesOnlyFromKnownProxies(bool trustLoopbackProxy, HttpStatusCode expected)
    {
        using var fixture = new Fixture();
        fixture.Owner.Start(false, false);
        await using var app = await StartAppAsync(fixture.Owner, timeout: TimeSpan.FromSeconds(5), runtime: new TimerBackendRuntimeOptions
        {
            ClientRequestsPerSecond = 1,
            ClientRequestBurst = 1,
            KnownProxies = trustLoopbackProxy ? [IPAddress.Loopback] : [],
        });
        using var client = new HttpClient { BaseAddress = Address(app) };

        async Task<HttpStatusCode> GetAs(string forwardedFor)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/maps");
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await GetAs("203.0.113.1"));
        Assert.Equal(expected, await GetAs("203.0.113.2"));
    }

    [Fact]
    public async Task ConditionalRequestsAfterExpiryRefillTheLeaderboardCache()
    {
        using var fixture = new Fixture();
        var map = await fixture.Store.GetMapInfo($"surf_cache_conditional_{Guid.NewGuid():N}");
        await fixture.Store.AddPlayerRecord(new SteamID(76561198000000012UL), map.MapName, new RecordRequest { Time = 80 });
        fixture.Owner.Start(false, false);
        await using var app = await StartAppAsync(fixture.Owner, timeout: TimeSpan.FromSeconds(5),
            leaderboardCacheDuration: TimeSpan.FromSeconds(1));
        using var client = new HttpClient { BaseAddress = Address(app) };
        var path = $"/api/v1/maps/{map.MapName}/leaderboard?limit=1";

        using var initial = await client.GetAsync(path);
        var entityTag = initial.Headers.ETag!.Tag;
        await Task.Delay(TimeSpan.FromMilliseconds(1500)); // let the entry expire

        HttpRequestMessage Conditional()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.TryAddWithoutValidation("If-None-Match", entityTag);
            return request;
        }

        // Only polling clients remain. The first miss must still refill the entry; a 304 is never cached.
        using var refill = await client.SendAsync(Conditional());
        Assert.Equal(HttpStatusCode.OK, refill.StatusCode);

        fixture.Store.Db.DbMaintenance.DropTable<RunEntity>();
        using var fromCache = await client.SendAsync(Conditional());
        Assert.Equal(HttpStatusCode.NotModified, fromCache.StatusCode);
    }

    [Fact]
    public async Task LeaderboardOutputCacheExpiresAndKeepsRouteKeysSeparate()
    {
        using var fixture = new Fixture();
        var map = await fixture.Store.GetMapInfo($"surf_cache_expiry_{Guid.NewGuid():N}");
        var player = new SteamID(76561198000000003UL);
        await fixture.Store.AddPlayerRecord(player, map.MapName, new RecordRequest { Time = 80 });
        await fixture.Store.AddPlayerStageRecord(new SteamID(76561198000000004UL), map.MapName,
            new RecordRequest { Stage = 1, Time = 60 });
        fixture.Owner.Start(false, false);
        await using var app = await StartAppAsync(fixture.Owner, timeout: TimeSpan.FromSeconds(5),
            leaderboardCacheDuration: TimeSpan.FromSeconds(1));
        using var client = new HttpClient { BaseAddress = Address(app) };
        var path = $"/api/v1/maps/{map.MapName}/leaderboard?limit=1";

        using var initial = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        Assert.Equal(80_000_000, await ReadFirstTimeMicrosAsync(initial));
        var initialTag = initial.Headers.ETag?.Tag;

        await fixture.Store.AddPlayerRecord(player, map.MapName, new RecordRequest { Time = 70 });
        using (var withinTtl = await client.GetAsync(path))
        {
            Assert.Equal(HttpStatusCode.OK, withinTtl.StatusCode);
            var time = await ReadFirstTimeMicrosAsync(withinTtl);
            Assert.Contains(time, new[] { 70_000_000L, 80_000_000L });
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        long latestTime = 0;
        string? latestTag = null;
        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            latestTime = await ReadFirstTimeMicrosAsync(response);
            latestTag = response.Headers.ETag?.Tag;
            if (latestTime == 70_000_000 && latestTag != initialTag) break;
            await Task.Delay(100);
        }

        Assert.Equal(70_000_000, latestTime);
        Assert.NotEqual(initialTag, latestTag);

        using var stage = await client.GetAsync(
            $"/api/v1/maps/{map.MapName}/stage-leaderboard?stage=1&limit=1");
        Assert.Equal(HttpStatusCode.OK, stage.StatusCode);
        Assert.Equal(60_000_000, await ReadFirstTimeMicrosAsync(stage));

        using var invalid = await client.GetAsync(
            $"/api/v1/maps/{map.MapName}/leaderboard?limit=0");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var missing = await client.GetAsync(
            "/api/v1/maps/surf_cache_missing/leaderboard?limit=1");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task LeaderboardOutputCacheServesConcurrentColdRequestsAndRecoversAfterFailure()
    {
        using var fixture = new Fixture();
        var map = await fixture.Store.GetMapInfo($"surf_cache_concurrent_{Guid.NewGuid():N}");
        await fixture.Store.AddPlayerRecord(new SteamID(76561198000000005UL), map.MapName,
            new RecordRequest { Time = 80 });
        fixture.Owner.Start(false, false);
        await using var app = await StartAppAsync(fixture.Owner, timeout: TimeSpan.FromSeconds(5));
        using var client = new HttpClient { BaseAddress = Address(app) };
        var basePath = $"/api/v1/maps/{map.MapName}/leaderboard";

        var responses = await Task.WhenAll(Enumerable.Range(0, 64)
            .Select(_ => client.GetAsync($"{basePath}?limit=1")));
        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync()));
            Assert.All(bodies, responseBody => Assert.Equal(bodies[0], responseBody));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }

        fixture.Store.Db.DbMaintenance.DropTable<RunEntity>();
        using var cached = await client.GetAsync($"{basePath}?limit=1");
        Assert.Equal(HttpStatusCode.OK, cached.StatusCode);

        using var differentKey = await client.GetAsync($"{basePath}?limit=2");
        Assert.Equal(HttpStatusCode.InternalServerError, differentKey.StatusCode);
    }

    [Fact]
    public async Task ReadRepairReadinessProbeLeavesNoRowsBehind()
    {
        using var fixture = new Fixture();
        fixture.Owner.Start(false, allowReadRepair: true);

        // The INSERT privilege probe writes a best-run row and must always roll it back.
        await fixture.Owner.CheckReadyAsync();
        await fixture.Owner.CheckReadyAsync();

        Assert.Equal(0, await fixture.Store.Db.Queryable<PlayerBestRunEntity>().CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadsReport503WhenTheDatabaseCannotAnswerAtAll()
    {
        using var fixture = new Fixture();
        fixture.Owner.Start(false, false);
        await using var app = await StartAppAsync(fixture.Owner, timeout: TimeSpan.FromSeconds(5));
        using var client = new HttpClient { BaseAddress = Address(app) };

        // With the map table gone even the trivial reachability query fails, which is how a
        // provider outage looks after SqlSugar has wrapped the driver exception.
        fixture.Store.Db.DbMaintenance.DropTable<MapEntity>();

        using var response = await client.GetAsync("/api/v1/maps");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("database_unavailable", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OneCorruptStoredRunDoesNotFailTheWholeLeaderboard()
    {
        using var fixture = new Fixture();
        var map = await fixture.Store.GetMapInfo($"surf_corrupt_row_{Guid.NewGuid():N}");
        var good = new SteamID(76561198000000007UL);
        var corrupt = new SteamID(76561198000000008UL);
        await fixture.Store.AddPlayerRecord(good, map.MapName, new RecordRequest { Time = 80 });
        await fixture.Store.AddPlayerRecord(corrupt, map.MapName, new RecordRequest { Time = 90 });
        await fixture.Store.Db.Updateable<RunEntity>()
                     .SetColumns(run => run.Time == -1f)
                     .Where(run => run.SteamId == unchecked((long)corrupt.AsPrimitive()))
                     .ExecuteCommandAsync();
        fixture.Owner.Start(false, false);
        await using var app = await StartAppAsync(fixture.Owner, timeout: TimeSpan.FromSeconds(5));
        using var client = new HttpClient { BaseAddress = Address(app) };

        using var response = await client.GetAsync($"/api/v1/maps/{map.MapName}/leaderboard");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var record = Assert.Single(document.RootElement.GetProperty("records").EnumerateArray());
        Assert.Equal(good.AsPrimitive().ToString(), record.GetProperty("steamId").GetString());
    }

    [Fact]
    public async Task AbortedLeaderboardRequestDoesNotFailConcurrentIdenticalRequests()
    {
        using var fixture = new Fixture();
        var map = await fixture.Store.GetMapInfo($"surf_cache_abort_{Guid.NewGuid():N}");
        await fixture.Store.AddPlayerRecord(new SteamID(76561198000000006UL), map.MapName,
            new RecordRequest { Time = 80 });
        fixture.Owner.Start(false, false);
        await using var app = await StartAppAsync(fixture.Owner, timeout: TimeSpan.FromSeconds(5), onRequest: () =>
            fixture.Store.Db.Aop.OnLogExecuting = (_, _) => Thread.Sleep(200));
        using var abortingClient = new HttpClient { BaseAddress = Address(app) };
        using var waitingClient = new HttpClient { BaseAddress = Address(app) };
        var path = $"/api/v1/maps/{map.MapName}/leaderboard?limit=1";

        // The first request owns the cold cache key and disconnects while its SQL is running.
        // A concurrent identical request must not inherit that client's cancellation.
        using var abort = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var aborted = abortingClient.GetAsync(path, abort.Token);
        await Task.Delay(50);
        using var waiting = await waitingClient.GetAsync(path);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => aborted);

        Assert.Equal(HttpStatusCode.OK, waiting.StatusCode);
        Assert.Equal(80_000_000, await ReadFirstTimeMicrosAsync(waiting));
    }

    [Fact]
    public async Task WorkerHealthAggregatePreservesPendingAgeAndEmptyState()
    {
        using var fixture = new Fixture(worker: true);
        fixture.Owner.Start(false, false);
        await fixture.Owner.StopWorkerAsync(default);
        var now = DateTime.UtcNow;
        var oldest = now.AddMinutes(-2);
        var newer = now.AddMinutes(-1);
        await fixture.Store.Db.Deleteable<ScoreRecalcOutboxEntity>().ExecuteCommandAsync();
        await fixture.Store.Db.Insertable(new[]
        {
            new ScoreRecalcOutboxEntity
            {
                MapId = 999, Style = 0, RequestedGeneration = 1, ProcessedGeneration = 0,
                StyleFactor = 1, PendingSinceUtc = null, AvailableAtUtc = now,
                CreatedAtUtc = oldest, UpdatedAtUtc = now,
            },
            new ScoreRecalcOutboxEntity
            {
                MapId = 999, Style = 1, RequestedGeneration = 2, ProcessedGeneration = 1,
                StyleFactor = 1, PendingSinceUtc = newer, AvailableAtUtc = now.AddMinutes(5),
                LeaseOwner = "future-worker", LeaseUntilUtc = now.AddMinutes(5),
                CreatedAtUtc = now, UpdatedAtUtc = now,
            },
            new ScoreRecalcOutboxEntity
            {
                MapId = 999, Style = 2, RequestedGeneration = 3, ProcessedGeneration = 3,
                StyleFactor = 1, AvailableAtUtc = now, DeadLetteredAtUtc = now,
                CreatedAtUtc = now, UpdatedAtUtc = now,
            },
            new ScoreRecalcOutboxEntity
            {
                MapId = 999, Style = 3, RequestedGeneration = 4, ProcessedGeneration = 4,
                StyleFactor = 1, AvailableAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now,
            },
        }).ExecuteCommandAsync();

        var sqlCalls = 0;
        fixture.Store.Db.Aop.OnLogExecuting = (_, _) => Interlocked.Increment(ref sqlCalls);
        try
        {
            var health = await fixture.Owner.GetWorkerHealthAsync();
            Assert.True(health.Enabled);
            Assert.Equal(2, health.PendingCount);
            Assert.Equal(1, health.DeadLetterCount);
            Assert.Equal(DateTimeKind.Utc, health.OldestPendingSinceUtc!.Value.Kind);
            Assert.InRange(Math.Abs((health.OldestPendingSinceUtc.Value - oldest).TotalSeconds), 0, 1);
            Assert.Equal(1, Volatile.Read(ref sqlCalls));

            await fixture.Store.Db.Deleteable<ScoreRecalcOutboxEntity>().ExecuteCommandAsync();
            Interlocked.Exchange(ref sqlCalls, 0);
            health = await fixture.Owner.GetWorkerHealthAsync();
            Assert.Equal(0, health.PendingCount);
            Assert.Equal(0, health.DeadLetterCount);
            Assert.Null(health.OldestPendingSinceUtc);
            Assert.Equal(1, Volatile.Read(ref sqlCalls));
        }
        finally
        {
            fixture.Store.Db.Aop.OnLogExecuting = null;
        }

        using var disabled = new Fixture();
        disabled.Owner.Start(false, false);
        var disabledSql = 0;
        disabled.Store.Db.Aop.OnLogExecuting = (_, _) => Interlocked.Increment(ref disabledSql);
        try
        {
            var health = await disabled.Owner.GetWorkerHealthAsync();
            Assert.False(health.Enabled);
            Assert.Equal(0, Volatile.Read(ref disabledSql));
        }
        finally
        {
            disabled.Store.Db.Aop.OnLogExecuting = null;
        }
    }

    [Theory]
    [InlineData("RequestTimeoutSeconds", "0")]
    [InlineData("WorkerShutdownTimeoutSeconds", "121")]
    [InlineData("WorkerMaxLagSeconds", "invalid")]
    public void InvalidRuntimeBudgetsFailFast(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [$"TimerBackend:Runtime:{key}"] = value }).Build();
        Assert.Throws<InvalidOperationException>(() => TimerBackendRuntimeOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void WorkerHealthDetectsStalledScansAndOldWorkWithoutDeadLetters()
    {
        var now = DateTime.UtcNow;
        var stalled = new TimerBackendWorkerHealth { Enabled = true, StartedAtUtc = now.AddMinutes(-10) };
        Assert.Equal("degraded", WorkerHealthPolicy.Evaluate(stalled, TimeSpan.FromMinutes(5), now).Status);
        var delayed = stalled with { LastSuccessfulScanUtc = now, PendingCount = 1, OldestPendingSinceUtc = now.AddMinutes(-10) };
        Assert.Equal("degraded", WorkerHealthPolicy.Evaluate(delayed, TimeSpan.FromMinutes(5), now).Status);
    }

    private static async Task<long> ReadFirstTimeMicrosAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("records").EnumerateArray().First()
            .GetProperty("timeMicros").GetInt64();
    }

    private static async Task<WebApplication> StartAppAsync(
        TimerBackendStorage storage,
        bool http2 = false,
        TimeSpan? timeout = null,
        TimeSpan? leaderboardCacheDuration = null,
        Action? onRequest = null,
        TimerBackendRuntimeOptions? runtime = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listener =>
            listener.Protocols = http2 ? HttpProtocols.Http2 : HttpProtocols.Http1));
        builder.Services.AddSingleton(storage);
        builder.Services.AddResponseCompression();
        builder.Services.ConfigureHttpJsonOptions(BackendJsonOptions.Configure);
        var runtimeOptions = (runtime ?? new TimerBackendRuntimeOptions()) with
        {
            RequestTimeout = timeout ?? runtime?.RequestTimeout ?? TimeSpan.FromMilliseconds(50),
        };
        TimerBackendRuntimeRegistration.Add(builder.Services, runtimeOptions, leaderboardCacheDuration);
        var write = WriteOptions(http2);
        builder.Services.AddSingleton(write);
        TimerWriteApiRegistration.Add(builder.Services, write);
        var app = builder.Build();
        // Same order as Program.cs.
        TimerBackendRuntimeRegistration.UseForwardedHeadersIfConfigured(app, runtimeOptions);
        app.UseExceptionHandler(exceptionApplication => exceptionApplication.Run(BackendExceptionHandling.WriteErrorAsync));
        app.Use(async (context, next) => { onRequest?.Invoke(); await next(context); });
        app.UseRouting();
        app.UseRequestTimeouts();
        app.UseOutputCache();
        app.UseResponseCompression();
        app.UseRateLimiter();
        TimerReadEndpoints.Map(app);
        TimerWriteApiRegistration.Map(app, write);
        await app.StartAsync();
        return app;
    }

    private static Uri Address(WebApplication app)
        => new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());

    private static TimerWriteApiOptions WriteOptions(bool enabled)
        => TimerWriteApiOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["TimerBackend:WriteApi:Enabled"] = enabled.ToString(),
                ["TimerBackend:WriteApi:AllowAllListeners"] = "true", // bound to port 0
            }).Build());

    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"timer-resilience-{Guid.NewGuid():N}.db");
        public StorageServiceImpl Store { get; }
        public TimerBackendStorage Owner { get; }

        public Fixture(bool worker = false)
        {
            Store = new StorageServiceImpl(DbType.Sqlite, $"Data Source={_path};Pooling=False",
                NullLogger<StorageServiceImpl>.Instance, enableScoreRecalcWorker: worker);
            Store.Db.CurrentConnectionConfig.ConfigureExternalServices.EntityService = (_, column) =>
            {
                if (column.IsIdentity) column.DataType = "INTEGER";
            };
            Store.Init(startScoreRecalcWorker: false);
            Owner = new TimerBackendStorage(Store);
        }

        public void Dispose()
        {
            Owner.StopWorkerAsync(default).GetAwaiter().GetResult();
            Owner.Dispose();
            File.Delete(_path);
        }
    }
}

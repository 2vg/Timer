using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using MagicOnion.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Backend.Rpc.Contracts;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.Backend.Configuration;
using Timer.Backend.Storage;
using Timer.Backend.WriteApi;
using Xunit;

namespace Timer.Backend.Tests;

// The game server's storage service end to end: gRPC, MessagePack and the SQL storage behind it.
public sealed class TimerStorageLoopbackTests : IAsyncLifetime
{
    private const ulong SteamId = 76561198000000042;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"timer-storage-rpc-{Guid.NewGuid():N}.db");
    private StorageServiceImpl  _store  = null!;
    private TimerBackendStorage _owner  = null!;
    private WebApplication      _app    = null!;
    private GrpcChannel         _channel = null!;
    private ITimerStorageServiceV1 _client = null!;

    public async Task InitializeAsync()
    {
        _store = new StorageServiceImpl(DbType.Sqlite, $"Data Source={_path};Pooling=False",
                                        NullLogger<StorageServiceImpl>.Instance, enableScoreRecalcWorker: false);
        _store.Db.CurrentConnectionConfig.ConfigureExternalServices.EntityService = (_, column) =>
        {
            if (column.IsIdentity) column.DataType = "INTEGER";
        };
        _store.Init(startScoreRecalcWorker: false);
        _owner = new TimerBackendStorage(_store);
        _owner.Start(false, false);
        (_app, _channel, _client) = await StartAsync(_owner, explicitStyleFactors: true);
    }

    public async Task DisposeAsync()
    {
        _channel.Dispose();
        await _app.DisposeAsync();
        _owner.Dispose();
        File.Delete(_path);
    }

    [Fact]
    public async Task RecordsZonesStatsAndReplaysRoundTrip()
    {
        var map = await _client.GetMapInfoAsync("surf_rpc", 0);
        Assert.True(map.MapId > 0);
        Assert.Equal(MapProfile.DefaultTrackCount, map.Tier.Length);

        await _store.GetPlayerProfile(new SteamID(SteamId), "Loopback");
        var (_, saved, _) = await _store.AddPlayerRecord(new SteamID(SteamId), "surf_rpc", new RecordRequest
        {
            Time = 80, Jumps = 3, VelocityStartX = 250,
            Checkpoints = [new RecordRequest.CheckpointRecord { CheckpointIndex = 1, Time = 40, VelocityMaxZ = 7 }],
        });

        var board = await _client.GetMapRecordsAsync("surf_rpc", 0, RunKind.Main, true, 0, 0, 0, 5000);
        var record = Assert.Single(board);
        Assert.Equal(saved.Id, record.Id);
        Assert.Equal(80f, record.Time);
        Assert.Equal(250f, record.StartX);
        Assert.Equal("Loopback", record.PlayerName);
        Assert.Single(await _client.GetMapRecordsAsync("surf_rpc", 0, RunKind.Main, false, 0, 0, 0, 10));
        Assert.Empty(await _client.GetMapRecordsAsync("surf_rpc", 0, RunKind.Stage, true, 0, 0, 0, 5000));
        Assert.Single(await _client.GetPlayerRecordsAsync(SteamId, "surf_rpc", 0, RunKind.Main));
        Assert.Single(await _client.GetRecentRecordsAsync("surf_rpc", 0, SteamId, 10));
        Assert.Equal(7f, Assert.Single(await _client.GetRecordCheckpointsAsync(record.Id)).Motion.MaxZ);

        await _client.SaveZonesAsync("surf_rpc", 0,
        [
            new ZoneDto { Type = 1, Track = 0, Mins = new VectorDto { X = -1 }, Maxs = new VectorDto { X = 1 },
                          Center = new VectorDto(), TeleportAngles = new VectorDto { Y = 90 } },
        ]);
        var zone = Assert.Single(await _client.GetZonesAsync("surf_rpc", 0));
        Assert.Equal(-1f, zone.Mins.X);
        Assert.Null(zone.TeleportOrigin);
        Assert.Equal(90f, zone.TeleportAngles!.Y);

        await _client.IncrementMapStatsAsync("surf_rpc", 0, 30);
        await _client.UpdatePlayerMapStatsAsync(SteamId, "surf_rpc", 0, 30);
        Assert.Equal(1, (await _client.GetMapInfoAsync("surf_rpc", 0)).PlayCount);
        Assert.Equal(1, (await _client.GetPlayerMapStatsAsync(SteamId, "surf_rpc", 0)).PlayCount);
        Assert.Contains(map.MapId, (await _client.GetCompletedMapsAsync(SteamId, 0, 0)).Keys);
        Assert.Equal(1, (await _client.GetPlayerSummaryAsync(SteamId))!.TotalMaps);
        Assert.Contains(await _client.GetMapProfilesAsync(), x => x.MapId == map.MapId);
        Assert.Contains("surf_rpc", await _client.GetAllMapNamesAsync());

        var runId = (ulong)record.Id;
        Assert.True(await _client.SaveReplayUrlAsync("surf_rpc", 0, SteamId, runId, "https://replays.test/a"));
        Assert.False(await _client.SaveReplayUrlAsync("surf_rpc", 0, SteamId, runId + 100, "https://replays.test/b"));
        Assert.Equal("https://replays.test/a", await _client.GetReplayUrlAsync("surf_rpc", 0, RunKind.Main, 0, 0, 0, null));
        Assert.Equal("https://replays.test/a", await _client.GetRunReplayUrlAsync(runId));
        Assert.Equal([runId], await _client.GetStoredReplayRunIdsAsync([runId, runId + 100]));

        var deleted = await _client.DeleteRunAsync("surf_rpc", 0, runId);
        Assert.NotNull(deleted);
        Assert.Equal((SteamId, RunKind.Main, true), (deleted.SteamId, deleted.Kind, deleted.WasBest));
        Assert.Equal(["https://replays.test/a"], deleted.ReplayUrls);
        Assert.Null(await _client.DeleteRunAsync("surf_rpc", 0, runId));
        Assert.Empty(await _client.GetMapRecordsAsync("surf_rpc", 0, RunKind.Main, true, 0, 0, 0, 5000));

        var (_, again, _) = await _store.AddPlayerRecord(new SteamID(SteamId), "surf_rpc", new RecordRequest { Time = 50 });
        var counted = await _client.WipePlayerRunsAsync(SteamId, true);
        Assert.Equal((1, 1, 0), (counted.Maps, counted.Runs, counted.Deleted.Length));
        var wiped = await _client.WipePlayerRunsAsync(SteamId, false);
        var run   = Assert.Single(wiped.Deleted);
        Assert.Equal(("surf_rpc", 50f, (ulong)again.Id, SteamId, true), (run.MapName, run.Time, run.Run.RunId, run.Run.SteamId, run.Run.WasBest));
        Assert.Empty(await _client.GetMapRecordsAsync("surf_rpc", 0, RunKind.Main, true, 0, 0, 0, 5000));

        await _client.RemoveMapRecordsAsync("surf_rpc", 0);
        Assert.Empty(await _client.GetMapRecordsAsync("surf_rpc", 0, RunKind.Main, true, 0, 0, 0, 5000));
    }

    // The zone panel saves the whole list on every edit, and requests are capped at 64 KiB.
    [Fact]
    public async Task FiveHundredZonesFitOneSave()
    {
        var zones = Enumerable.Range(0, 500)
                              .Select(i => new ZoneDto
                              {
                                  Id = ulong.MaxValue - (ulong)i, Type = 1, Track = i % 64, Sequence = i,
                                  Mins = new VectorDto { X = -12345.678f, Y = -12345.678f, Z = -12345.678f },
                                  Maxs = new VectorDto { X = 12345.678f, Y = 12345.678f, Z = 12345.678f },
                                  Center = new VectorDto { X = 0.5f, Y = 0.5f, Z = 0.5f },
                                  TeleportOrigin = new VectorDto { X = 1.5f, Y = 1.5f, Z = 1.5f },
                                  TeleportAngles = new VectorDto { X = 2.5f, Y = 2.5f, Z = 2.5f },
                              })
                              .ToArray();

        await _client.SaveZonesAsync("surf_rpc_zones", 0, zones);

        Assert.Equal(500, (await _client.GetZonesAsync("surf_rpc_zones", 0)).Length);
    }

    [Fact]
    public async Task TierChangesAndRecalculationsUseTheBackendPolicy()
    {
        var missing = await _client.SetMapTierAsync("surf_rpc_missing", 0, 3);
        Assert.False(missing.MapFound);

        await _client.GetMapInfoAsync("surf_rpc_tier", 0);
        var tier = await _client.SetMapTierAsync("surf_rpc_tier", 0, 3);
        Assert.True(tier.MapFound);
        Assert.Equal((byte)3, tier.CurrentTier);
        Assert.Equal(3, (await _client.GetMapInfoAsync("surf_rpc_tier", 0)).Tier[0]);

        Assert.True((await _client.RecalculateScoresAsync("surf_rpc_tier", 0)).MapFound);
        Assert.True((await _client.RecalculateScoresAsync(null, 0)).MapFound);
    }

    [Fact]
    public async Task TierChangesNeedAnExplicitPolicy()
    {
        var (app, channel, client) = await StartAsync(_owner, explicitStyleFactors: false);
        await using var _ = app;
        using var __ = channel;

        var error = await Assert.ThrowsAsync<RpcException>(async () => await client.SetMapTierAsync("surf_rpc_policy", 0, 3));
        Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
    }

    [Fact]
    public async Task AWorkshopMapKeepsItsDataAcrossARename()
    {
        var original = await _client.GetMapInfoAsync("surf_rpc_ws", 123);
        Assert.Equal(123UL, original.WorkshopId);

        // A read that reaches the backend before the renamed map's GetMapInfo still finds it.
        Assert.Empty(await _client.GetZonesAsync("surf_rpc_ws_v2", 123));
        var renamed = await _client.GetMapInfoAsync("surf_rpc_ws_v2", 123);
        Assert.Equal(original.MapId, renamed.MapId);
        Assert.Equal("surf_rpc_ws_v2", renamed.MapName);

        // Another server's binding never leaks into a request without one.
        Assert.NotEqual(original.MapId, (await _client.GetMapInfoAsync("surf_rpc_other", 0)).MapId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("surf rpc")]
    [InlineData("surf_édge")]
    public async Task InvalidMapNamesAreRejected(string mapName)
    {
        var error = await Assert.ThrowsAsync<RpcException>(async () => await _client.GetMapInfoAsync(mapName, 0));
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    }

    [Fact]
    public async Task ReadsWithACallerChosenWorkshopItemNeverRebindOrRenameMaps()
    {
        var unbound = await _client.GetMapInfoAsync("surf_rpc_unbound", 0);
        var bound = await _client.GetMapInfoAsync("surf_rpc_bound", 555);

        // Reads with another item id: the unbound map must not be claimed, the bound one must not be renamed.
        await _client.GetZonesAsync("surf_rpc_unbound", 999);
        await _client.GetMapRecordsAsync("surf_rpc_hijack", 555, RunKind.Main, true, 0, 0, 0, 10);
        await _client.GetZonesAsync("surf_rpc_hijack", 555);

        var profiles = await _client.GetMapProfilesAsync();
        Assert.Equal(0UL, profiles.Single(x => x.MapId == unbound.MapId).WorkshopId);
        Assert.Equal("surf_rpc_bound", profiles.Single(x => x.MapId == bound.MapId).MapName);
        Assert.DoesNotContain(profiles, x => x.MapName == "surf_rpc_hijack");

        // The resolved-but-not-renamed row was not cached under the read's name.
        Assert.NotEqual(bound.MapId, (await _client.GetMapInfoAsync("surf_rpc_hijack", 0)).MapId);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://replays.test/a")]
    [InlineData("https://user:secret@replays.test/a")]
    [InlineData("https://elsewhere.test/a")]
    [InlineData("https://replays.test.evil/a")]
    public async Task ReplayUrlsOutsideTheConfiguredStoreAreRejected(string url)
    {
        var (app, channel, client) = await StartAsync(_owner, explicitStyleFactors: true, new()
        {
            ["TimerBackend:WriteApi:ReplayUrlPrefixes:0"] = "https://replays.test/",
        });
        await using var _ = app;
        using var __ = channel;

        var error = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.SaveReplayUrlAsync("surf_rpc", 0, SteamId, 1, url));
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);

        // A link into the store passes validation (false: no such run) and never creates the map.
        Assert.False(await client.SaveReplayUrlAsync("surf_rpc_replay_missing", 0, SteamId, 1, "https://replays.test/a"));
        Assert.DoesNotContain(await client.GetMapProfilesAsync(), x => x.MapName == "surf_rpc_replay_missing");
    }

    [Fact]
    public async Task OutOfRangeBoardsAndPlayTimesAreInvalidArguments()
    {
        await _client.GetMapInfoAsync("surf_rpc_bounds", 0);

        async Task<StatusCode> Status(Func<Task> call) => (await Assert.ThrowsAsync<RpcException>(call)).StatusCode;

        Assert.Equal(StatusCode.InvalidArgument, await Status(async () =>
            await _client.GetMapRecordsAsync("surf_rpc_bounds", 0, RunKind.Main, false, 64, 0, 0, 10)));
        Assert.Equal(StatusCode.InvalidArgument, await Status(async () =>
            await _client.GetPlayerRunsAsync("surf_rpc_bounds", 0, SteamId, 0, -1, 0, 10)));
        Assert.Equal(StatusCode.InvalidArgument, await Status(async () =>
            await _client.IncrementMapStatsAsync("surf_rpc_bounds", 0, float.NaN)));
        Assert.Equal(StatusCode.InvalidArgument, await Status(async () =>
            await _client.UpdatePlayerMapStatsAsync(SteamId, "surf_rpc_bounds", 0, float.MaxValue)));
        Assert.Equal(StatusCode.InvalidArgument, await Status(async () =>
            await _client.GetStoredReplayRunIdsAsync(Enumerable.Range(1, 1001).Select(x => (ulong)x).ToArray())));
    }

    internal static async Task<(WebApplication, GrpcChannel, ITimerStorageServiceV1)> StartAsync(
        TimerBackendStorage storage, bool explicitStyleFactors, Dictionary<string, string?>? extraSettings = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["TimerBackend:WriteApi:Enabled"] = "true",
            ["TimerBackend:WriteApi:AllowAllListeners"] = "true", // bound to port 0
        };
        if (explicitStyleFactors)
        {
            settings["TimerBackend:WriteApi:StyleFactors:0"] = "1";
        }

        foreach (var (key, value) in extraSettings ?? [])
        {
            settings[key] = value;
        }

        var options = TimerWriteApiOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.SetMinimumLevel(LogLevel.None);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
            listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddSingleton(storage);
        builder.Services.AddSingleton(options);
        TimerWriteApiRegistration.Add(builder.Services, options);

        var app = builder.Build();
        TimerWriteApiRegistration.Map(app, options);
        await app.StartAsync();

        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        var channel = GrpcChannel.ForAddress(address);

        return (app, channel, MagicOnionClient.Create<ITimerStorageServiceV1>(channel));
    }
}

using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Storage.Tests;

// What the map chooser reads: every map with its tiers and workshop item, and which maps a player has finished.
// "Every map, adding none" is checked by counting the whole table, so no other test may create maps meanwhile.
[Collection(SqlSchemaMutationCollection.Name)]
public sealed class MapChooserStorageTests
{
    [Fact]
    public Task SqliteMapProfilesListEveryMapWithoutAddingAny()
        => MapProfilesListEveryMapWithoutAddingAny(DbType.Sqlite, null);

    [DatabaseFact("TIMER_TEST_MYSQL")]
    public Task MySqlMapProfilesListEveryMapWithoutAddingAny()
        => MapProfilesListEveryMapWithoutAddingAny(DbType.MySql, "TIMER_TEST_MYSQL");

    [DatabaseFact("TIMER_TEST_POSTGRES")]
    public Task PostgreSqlMapProfilesListEveryMapWithoutAddingAny()
        => MapProfilesListEveryMapWithoutAddingAny(DbType.PostgreSQL, "TIMER_TEST_POSTGRES");

    [Fact]
    public Task SqliteCompletedMapsAreMainFinishesOnOneStyleAndTrack()
        => CompletedMapsAreMainFinishesOnOneStyleAndTrack(DbType.Sqlite, null);

    [DatabaseFact("TIMER_TEST_MYSQL")]
    public Task MySqlCompletedMapsAreMainFinishesOnOneStyleAndTrack()
        => CompletedMapsAreMainFinishesOnOneStyleAndTrack(DbType.MySql, "TIMER_TEST_MYSQL");

    [DatabaseFact("TIMER_TEST_POSTGRES")]
    public Task PostgreSqlCompletedMapsAreMainFinishesOnOneStyleAndTrack()
        => CompletedMapsAreMainFinishesOnOneStyleAndTrack(DbType.PostgreSQL, "TIMER_TEST_POSTGRES");

    private static async Task MapProfilesListEveryMapWithoutAddingAny(DbType type, string? variable)
    {
        using var fixture = new Fixture(type, variable);
        var store = fixture.Store;
        var prefix = $"surf_mc_{Guid.NewGuid():N}";

        var a = await store.GetMapInfo($"{prefix}_a");
        var b = await store.GetMapInfo($"{prefix}_b");

        a.Tier[0] = 3;
        a.Tier[1] = 5;
        await store.UpdateMapInfo(a);
        await store.Db.Updateable<MapEntity>()
                   .SetColumns(x => x.WorkshopId == 3090873045UL)
                   .Where(x => x.MapId == b.MapId)
                   .ExecuteCommandAsync();

        var before = await store.Db.Queryable<MapEntity>().CountAsync();
        var profiles = await store.GetMapProfilesAsync();

        Assert.Equal(before, await store.Db.Queryable<MapEntity>().CountAsync());
        Assert.Equal(before, profiles.Count);

        var mine = profiles.Where(x => x.MapName.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        Assert.Equal([a.MapName, b.MapName], mine.Select(x => x.MapName));
        Assert.Equal(a.MapId, mine[0].MapId);
        Assert.Equal(3, mine[0].Tier[0]);
        Assert.Equal(5, mine[0].Tier[1]);
        Assert.Equal(0UL, mine[0].WorkshopId);
        Assert.Equal(3090873045UL, mine[1].WorkshopId);
        Assert.Equal(1, mine[1].Tier[0]);
    }

    private static async Task CompletedMapsAreMainFinishesOnOneStyleAndTrack(DbType type, string? variable)
    {
        using var fixture = new Fixture(type, variable);
        var store = fixture.Store;
        var player = Fixture.Player();
        var other = Fixture.Player();
        var prefix = $"surf_mc_{Guid.NewGuid():N}";

        var a = await store.GetMapInfo($"{prefix}_a");
        var b = await store.GetMapInfo($"{prefix}_b");
        var c = await store.GetMapInfo($"{prefix}_c");
        var d = await store.GetMapInfo($"{prefix}_d");

        // a twice (the faster one counts), b on another style, c only on a bonus, d only a stage; other's map is e.
        await store.AddPlayerRecord(player, a.MapName, new RecordRequest { Time = 30 });
        await store.AddPlayerRecord(player, a.MapName, new RecordRequest { Time = 25 });
        await store.AddPlayerRecord(player, b.MapName, new RecordRequest { Style = 1, Time = 40 });
        await store.AddPlayerRecord(player, c.MapName, new RecordRequest { Track = 1, Time = 12 });
        await store.AddPlayerStageRecord(player, d.MapName, new RecordRequest { Stage = 2, Time = 9 });
        await store.AddPlayerRecord(other, $"{prefix}_e", new RecordRequest { Time = 20 });

        Assert.Equal(new Dictionary<ulong, float> { [a.MapId] = 25 }, await store.GetCompletedMapsAsync(player, 0, 0));
        Assert.Equal(new Dictionary<ulong, float> { [b.MapId] = 40 }, await store.GetCompletedMapsAsync(player, 1, 0));
        Assert.Equal(new Dictionary<ulong, float> { [c.MapId] = 12 }, await store.GetCompletedMapsAsync(player, 0, 1));
        Assert.Empty(await store.GetCompletedMapsAsync(Fixture.Player(), 0, 0));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string? _path;
        private readonly string _connection;
        private readonly DbType _type;
        internal StorageServiceImpl Store { get; }

        internal Fixture(DbType type, string? variable)
        {
            _type = type;
            if (type == DbType.Sqlite)
            {
                _path = Path.Combine(Path.GetTempPath(), $"timer-map-chooser-{Guid.NewGuid():N}.db");
                _connection = $"Data Source={_path};Pooling=False";
            }
            else
            {
                _connection = Environment.GetEnvironmentVariable(variable!)!;
                var parsed = new DbConnectionStringBuilder { ConnectionString = _connection };
                var host = Convert.ToString(parsed[type == DbType.MySql ? "Server" : "Host"]);
                Assert.True(host is "127.0.0.1" or "localhost" or "::1"
                    && Convert.ToString(parsed["Database"])!.Contains("test", StringComparison.OrdinalIgnoreCase));
            }

            Store = new StorageServiceImpl(type, _connection, NullLogger<StorageServiceImpl>.Instance, false);
            if (type == DbType.Sqlite)
                Store.Db.CurrentConnectionConfig.ConfigureExternalServices.EntityService = (_, column) =>
                { if (column.IsIdentity) column.DataType = "INTEGER"; };
            else
                Store.Db.DbMaintenance.CreateDatabase();
            Store.Init(startScoreRecalcWorker: false);
        }

        internal static SteamID Player() => new(76561198000000000UL + checked((ulong)Random.Shared.NextInt64(1, 1_000_000_000)));

        public void Dispose()
        {
            Store.Shutdown();
            if (_path is not null && File.Exists(_path)) File.Delete(_path);
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

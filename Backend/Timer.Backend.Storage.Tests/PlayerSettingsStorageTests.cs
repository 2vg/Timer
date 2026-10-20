using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using SqlSugar;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Storage.Tests;

// Player settings: one binary row per player, replaced on save and removed when reset to defaults.
public sealed class PlayerSettingsStorageTests
{
    [Fact]
    public Task SqlitePlayerSettingsRoundTrip()
        => PlayerSettingsRoundTrip(DbType.Sqlite, null);

    [DatabaseFact("TIMER_TEST_MYSQL")]
    public Task MySqlPlayerSettingsRoundTrip()
        => PlayerSettingsRoundTrip(DbType.MySql, "TIMER_TEST_MYSQL");

    [DatabaseFact("TIMER_TEST_POSTGRES")]
    public Task PostgreSqlPlayerSettingsRoundTrip()
        => PlayerSettingsRoundTrip(DbType.PostgreSQL, "TIMER_TEST_POSTGRES");

    private static async Task PlayerSettingsRoundTrip(DbType type, string? variable)
    {
        using var fixture = new Fixture(type, variable);
        var store  = fixture.Store;
        var player = Fixture.Player();
        var other  = Fixture.Player();

        Assert.Null(await store.GetPlayerSettingsAsync(player));

        byte[] first = [1, 2, 3, 0, 7, 0, 0];
        await store.SavePlayerSettingsAsync(player, first);
        Assert.Equal(first, await store.GetPlayerSettingsAsync(player));

        // The largest payload the RPC accepts, with every byte value.
        var full = Enumerable.Range(0, 256).Select(i => (byte) i).ToArray();
        await store.SavePlayerSettingsAsync(player, full);
        Assert.Equal(full, await store.GetPlayerSettingsAsync(player));

        await store.SavePlayerSettingsAsync(other, first);
        Assert.Equal(1, await store.Db.Queryable<PlayerSettingsEntity>().Where(x => x.SteamId == (long) player.AsPrimitive()).CountAsync());

        await store.SavePlayerSettingsAsync(player, []);
        Assert.Null(await store.GetPlayerSettingsAsync(player));
        Assert.Equal(first, await store.GetPlayerSettingsAsync(other));

        // Resetting a player with no row is a no-op.
        await store.SavePlayerSettingsAsync(player, []);
        Assert.Null(await store.GetPlayerSettingsAsync(player));
    }

    [Fact]
    public Task SqliteStyleFactorsAreUpserted()
        => StyleFactorsAreUpserted(DbType.Sqlite, null);

    [DatabaseFact("TIMER_TEST_MYSQL")]
    public Task MySqlStyleFactorsAreUpserted()
        => StyleFactorsAreUpserted(DbType.MySql, "TIMER_TEST_MYSQL");

    [DatabaseFact("TIMER_TEST_POSTGRES")]
    public Task PostgreSqlStyleFactorsAreUpserted()
        => StyleFactorsAreUpserted(DbType.PostgreSQL, "TIMER_TEST_POSTGRES");

    // Game servers register their styles' score factors; a server without a style leaves it as it was.
    private static async Task StyleFactorsAreUpserted(DbType type, string? variable)
    {
        using var fixture = new Fixture(type, variable);
        var store = fixture.Store;

        await store.Db.Deleteable<StyleFactorEntity>().ExecuteCommandAsync();
        Assert.Empty(await store.GetStyleFactorsAsync());

        await store.SaveStyleFactorsAsync(new Dictionary<int, double> { [0] = 1, [1] = 0.5 });
        await store.SaveStyleFactorsAsync(new Dictionary<int, double> { [1] = 0.25, [2] = 0 });
        // A caller that may not change the shared policy can only add styles nobody registered yet.
        await store.SaveStyleFactorsAsync(new Dictionary<int, double> { [0] = 100, [3] = 2 }, overwriteExisting: false);

        var factors = await store.GetStyleFactorsAsync();
        Assert.Equal(4, factors.Count);
        Assert.Equal(1, factors[0]);
        Assert.Equal(0.25, factors[1]);
        Assert.Equal(0, factors[2]);
        Assert.Equal(2, factors[3]);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string? _path;
        internal StorageServiceImpl Store { get; }

        internal Fixture(DbType type, string? variable)
        {
            string connection;

            if (type == DbType.Sqlite)
            {
                _path      = Path.Combine(Path.GetTempPath(), $"timer-player-settings-{Guid.NewGuid():N}.db");
                connection = $"Data Source={_path};Pooling=False";
            }
            else
            {
                connection = Environment.GetEnvironmentVariable(variable!)!;
                var parsed = new DbConnectionStringBuilder { ConnectionString = connection };
                var host   = Convert.ToString(parsed[type == DbType.MySql ? "Server" : "Host"]);
                Assert.True(host is "127.0.0.1" or "localhost" or "::1"
                            && Convert.ToString(parsed["Database"])!.Contains("test", StringComparison.OrdinalIgnoreCase));
            }

            Store = new StorageServiceImpl(type, connection, NullLogger<StorageServiceImpl>.Instance, false);
            if (type == DbType.Sqlite)
                Store.Db.CurrentConnectionConfig.ConfigureExternalServices.EntityService = (_, column) =>
                { if (column.IsIdentity) column.DataType = "INTEGER"; };
            else
                Store.Db.DbMaintenance.CreateDatabase();
            Store.Init(startScoreRecalcWorker: false);
        }

        internal static SteamID Player() => new(76561198000000000UL + checked((ulong) Random.Shared.NextInt64(1, 1_000_000_000)));

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

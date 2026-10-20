using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Npgsql;
using Source2Surf.Timer.Common.Entities;
using SqlSugar;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Storage.Tests;

public sealed class SqlLoggingTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"timer-sql-logging-{Guid.NewGuid():N}.db");
    private readonly CapturingLogger _logger = new ();
    private readonly StorageServiceImpl _storage;

    public SqlLoggingTests()
    {
        _storage = new StorageServiceImpl(DbType.Sqlite,
                                          $"Data Source={_path};Pooling=False",
                                          _logger,
                                          enableScoreRecalcWorker: false);
        _storage.Db.CurrentConnectionConfig.ConfigureExternalServices.EntityService = (_, column) =>
        {
            if (column.IsIdentity) column.DataType = "INTEGER";
        };
        _storage.Init();
        _logger.Entries.Clear();
    }

    [Fact]
    public async Task FailedSqlOnTheSharedScopeIsLoggedWithItsStatement()
    {
        await Assert.ThrowsAnyAsync<Exception>(() =>
            _storage.Db.Queryable<MapEntity>().AS("surf_missing_table").ToListAsync(CancellationToken.None));

        var entry = Assert.Single(_logger.Entries, x => x.Message.StartsWith("SQL command failed", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("surf_missing_table", entry.Message);
    }

    [Fact]
    public async Task FailedSqlInsideAnOperationIsLogged()
    {
        _storage.Db.DbMaintenance.DropTable("surf_maps");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            _storage.RunOperationAsync(() => _storage.GetMapInfo("surf_logging_missing"), CancellationToken.None));

        Assert.Contains(_logger.Entries, x => x.Level == LogLevel.Warning
                                              && x.Message.StartsWith("SQL command failed", StringComparison.Ordinal)
                                              && x.Message.Contains("surf_maps"));
    }

    [Fact]
    public async Task OperationClientsLogSlowSqlOnceAndKeepForeignHandlers()
    {
        _storage.SlowSqlThreshold = TimeSpan.Zero;
        var foreignCalls = 0;
        Action<string, SugarParameter[]> foreign = (_, _) => Interlocked.Increment(ref foreignCalls);
        var hooks = _storage.Db.CurrentConnectionConfig.AopEvents;
        hooks.OnLogExecuted += foreign;
        try
        {
            await _storage.RunOperationAsync(() => _storage.GetMapInfo($"surf_logging_{Guid.NewGuid():N}"),
                                             CancellationToken.None);
        }
        finally { hooks.OnLogExecuted -= foreign; }

        var slow = _logger.Entries.Count(x => x.Message.StartsWith("Slow SQL", StringComparison.Ordinal));
        Assert.True(foreignCalls > 0);
        Assert.Equal(foreignCalls, slow);
    }

    [Fact]
    public async Task SlowSqlOnTheSharedScopeIsLogged()
    {
        // Startup, migrations and readiness checks run on the shared scope, outside any operation.
        _storage.SlowSqlThreshold = TimeSpan.Zero;
        await _storage.GetMapInfo($"surf_logging_scope_{Guid.NewGuid():N}");

        Assert.Contains(_logger.Entries, x => x.Message.StartsWith("Slow SQL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoggedSqlDoesNotContainParameterValues()
    {
        _storage.SlowSqlThreshold = TimeSpan.Zero;
        var mapName = $"surf_logging_private_{Guid.NewGuid():N}";
        await _storage.GetMapInfo(mapName);

        Assert.Contains(_logger.Entries, x => x.Message.StartsWith("Slow SQL", StringComparison.Ordinal)
                                              && x.Message.Contains("values redacted", StringComparison.Ordinal));
        Assert.DoesNotContain(_logger.Entries, x => x.Message.Contains(mapName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FastSqlIsNotLogged()
    {
        await _storage.GetMapInfo($"surf_logging_fast_{Guid.NewGuid():N}");

        Assert.DoesNotContain(_logger.Entries, x => x.Level >= LogLevel.Warning);
    }

    [Theory]
    [InlineData("23505")]
    [InlineData("40001")]
    [InlineData("40P01")]
    public void PostgresRacesAreRecognized(string sqlState)
        => Assert.True(StorageServiceImpl.IsHandledSqlRace(
            new PostgresException("conflict", "ERROR", "ERROR", sqlState).Message));

    [Theory]
    [InlineData("Duplicate entry '1' for key 'surf_maps.PRIMARY'", true)]
    [InlineData("Deadlock found when trying to get lock; try restarting transaction", true)]
    [InlineData("Lock wait timeout exceeded; try restarting transaction", true)]
    [InlineData("Table 'timer.surf_maps' doesn't exist", false)]
    public void MySqlRacesAreRecognized(string message, bool expected)
        => Assert.Equal(expected, StorageServiceImpl.IsHandledSqlRace(message));

    public void Dispose()
    {
        _storage.Shutdown();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // SQLite cleanup is best effort on a locked test runner connection.
        }
    }

    private sealed class CapturingLogger : ILogger<StorageServiceImpl>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new ();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((logLevel, formatter(state, exception)));
    }
}

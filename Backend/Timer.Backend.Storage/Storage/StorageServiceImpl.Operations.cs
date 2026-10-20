using System;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace Timer.Backend.Storage;

internal sealed partial class StorageServiceImpl
{
    // One token per asynchronous operation, never a mutable token shared by requests.
    // Every typed ORM call supplies this token explicitly, including CancellationToken.None
    // for legacy callers: SqlSugar otherwise retains the previous command's token.
    private readonly AsyncLocal<CancellationToken> _operationCancellation = new();
    private readonly AsyncLocal<SqlSugarClient?> _operationDb = new();

    private CancellationToken OperationCancellation
    {
        get
        {
            var token = _operationCancellation.Value;
            token.ThrowIfCancellationRequested();
            return token;
        }
    }

    // Building a client (SqlSugarScope.CopyNew) walks the stack and costs ~0.5 ms and ~360 KiB, so operations
    // borrow one from here instead. Each client serves one operation at a time.
    private const int MaxIdleClients = 128;
    // SqlSugar's own default, for operations without a deadline (CLI, worker).
    private const int CommandTimeoutSeconds = 300;
    // Requests carry their deadline in the token, but cancelling a MySQL command only sends KILL QUERY and
    // leaves the socket open; this backstop still closes a connection that stops answering.
    private const int RequestCommandTimeoutSeconds = 30;
    private readonly ConcurrentBag<SqlSugarClient> _idleClients = new();
    private int _idleClientCount;

    /// <param name="deadlineInToken">
    /// The token carries the operation's deadline, so commands get only the backstop timeout.
    /// </param>
    internal async Task<T> RunOperationAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken,
                                                bool deadlineInToken = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // An operation never shares its client with sibling tasks of the same execution context.
        // Every request owns its connection, transaction and token.
        var database = RentClient();
        database.Ado.CommandTimeOut = deadlineInToken ? RequestCommandTimeoutSeconds : CommandTimeoutSeconds;
        var previousDb = _operationDb.Value;
        _operationDb.Value = database;
        var previous = _operationCancellation.Value;
        _operationCancellation.Value = cancellationToken;
        var faulted = true;
        try
        {
            // SqlSugar's async commands open a closed connection synchronously, and with auto-close every
            // statement re-acquires one from the pool. Under load that blocks thread-pool threads on pool
            // waits (latency spikes of seconds while the pool grows threads) and resets the session per
            // statement. One asynchronous open per operation removes both; the pool resets the session
            // when the connection is returned.
            database.CurrentConnectionConfig.IsAutoCloseConnection = false;
            await ((DbConnection)database.Ado.Connection).OpenAsync(cancellationToken);

            var result = await operation();
            faulted = false;
            return result;
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested
                                          && exception is not OperationCanceledException)
        {
            // Providers may wrap their cancellation exception. Preserve the transport's
            // cancellation status only after the operation has finished its rollback.
            throw new OperationCanceledException("The storage operation was cancelled.", exception, cancellationToken);
        }
        finally
        {
            _operationCancellation.Value = previous;
            _operationDb.Value = previousDb;
            await CloseOperationConnectionAsync(database);
            ReturnClient(database, faulted);
        }
    }

    private async Task CloseOperationConnectionAsync(SqlSugarClient database)
    {
        database.CurrentConnectionConfig.IsAutoCloseConnection = true;
        try
        {
            if (database.Ado.Transaction is null && database.Ado.Connection.State != ConnectionState.Closed)
            {
                await ((DbConnection)database.Ado.Connection).CloseAsync();
            }
        }
        catch (Exception exception)
        {
            // A broken connection fails to close; ReturnClient then discards the client.
            _logger.LogDebug(exception, "Closing an operation's connection failed.");
        }
    }

    private SqlSugarClient RentClient()
    {
        if (_idleClients.TryTake(out var client))
        {
            Interlocked.Decrement(ref _idleClientCount);
            return client;
        }

        client = _rootDb.CopyNew();
        ConfigureSqlLogging(client);
        return client;
    }

    private void ReturnClient(SqlSugarClient client, bool faulted)
    {
        // SqlSugar keeps the last command's token; it must not cancel the next operation's commands.
        client.Ado.CancellationToken = null;

        // A failed operation can leave its connection open or broken, and SqlSugar does not reopen a
        // broken one; only a client whose connection was closed cleanly is reused.
        if (!faulted
            && client.Ado.Transaction is null
            && client.Ado.Connection.State == ConnectionState.Closed
            && Volatile.Read(ref _shutdown) == 0)
        {
            if (Interlocked.Increment(ref _idleClientCount) <= MaxIdleClients)
            {
                _idleClients.Add(client);
                return;
            }

            Interlocked.Decrement(ref _idleClientCount);
        }

        client.Dispose();
    }

    private void DisposeIdleClients()
    {
        while (_idleClients.TryTake(out var client))
        {
            Interlocked.Decrement(ref _idleClientCount);
            client.Dispose();
        }
    }

    internal Task RunOperationAsync(Func<Task> operation, CancellationToken cancellationToken)
        => RunOperationAsync(async () => { await operation(); return true; }, cancellationToken);
}

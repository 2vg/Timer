using System;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace Timer.Backend.Storage;

internal sealed partial class StorageServiceImpl
{
    internal TimeSpan SlowSqlThreshold { get; set; } = TimeSpan.FromSeconds(1);
    private const int MaxLoggedSqlLength = 4096;

    // Every client of this storage, per-flow and pooled, shares one set of hooks: SqlSugar reads them per
    // command, so a hook added through Db reaches operations too.
    private AopEvents CreateSqlHooks()
        => new () { OnError = LogSqlError, OnLogExecuted = LogSlowSql };

    // SqlSugarScope runs this for each per-flow client it creates. Hooks run only while log events are on.
    private void ConfigureSqlLogging(SqlSugarClient client)
    {
        client.CurrentConnectionConfig.AopEvents = _sqlHooks;
        client.Ado.IsEnableLogEvent = true;
    }

    private void LogSlowSql(string sql, SugarParameter[] parameters)
    {
        // An operation's own client ran the command; outside one, the flow's scoped client did.
        var elapsed = (_operationDb.Value?.Ado ?? _rootDb.Ado).SqlExecutionTime;
        if (elapsed < SlowSqlThreshold || !_logger.IsEnabled(LogLevel.Warning)) return;

        _logger.LogWarning("Slow SQL took {ElapsedMs} ms:{Sql}", (long) elapsed.TotalMilliseconds,
                           FormatSqlForLog(sql, parameters));
    }

    private void LogSqlError(SqlSugarException error)
    {
        // A cancelled request is reported by its caller, not as a SQL failure.
        if (_operationCancellation.Value.IsCancellationRequested) return;

        var level = IsHandledSqlRace(error.Message) ? LogLevel.Debug : LogLevel.Warning;
        // Handled races log at Debug, normally off: skip formatting the statement for every one.
        if (!_logger.IsEnabled(level)) return;

        _logger.Log(level, "SQL command failed: {Error}{Sql}", error.Message,
                    FormatSqlForLog(error.Sql, error.Parametres as SugarParameter[]));
    }

    // SqlSugarException keeps only the provider's message, not the driver exception, so races the
    // callers retry or resolve (unique key, deadlock, lock timeout) are recognized by message text.
    // A miss only raises the log level.
    internal static bool IsHandledSqlRace(string message)
        => message.StartsWith("23505:", StringComparison.Ordinal)
           || message.StartsWith("40001:", StringComparison.Ordinal)
           || message.StartsWith("40P01:", StringComparison.Ordinal)
           || message.StartsWith("Duplicate entry", StringComparison.Ordinal)
           || message.StartsWith("Deadlock found", StringComparison.Ordinal)
           || message.StartsWith("Lock wait timeout exceeded", StringComparison.Ordinal);

    // The parameterized statement and its parameter names, never their values: those carry player names, settings,
    // zone configs and replay URLs (personal data, and control characters a client could inject into log lines).
    private static string FormatSqlForLog(string? sql, SugarParameter[]? parameters)
    {
        var text = sql ?? string.Empty;
        if (parameters is { Length: > 0 })
        {
            text = $"{text} [{parameters.Length} parameters, values redacted]";
        }

        return text.Length <= MaxLoggedSqlLength ? text : $"{text[..MaxLoggedSqlLength]}... ({text.Length} chars)";
    }
}

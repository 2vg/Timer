using System;
using System.Data.Common;
using System.Net;
using Microsoft.Extensions.Logging;

namespace Timer.Backend.Infrastructure;

/// <summary>
/// Warns at startup when the database is on another host but the connection string does not verify the server's
/// certificate. Both drivers default to opportunistic TLS (Preferred/Prefer), which an on-path attacker can strip.
/// </summary>
internal static class DatabaseTransportCheck
{
    public static void LogIfUnverified(ILogger logger, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (!TryFindRemoteHost(connectionString, out var host, out var sslMode))
        {
            return;
        }

        logger.LogWarning(
            "The database host {Host} is not loopback and SslMode is {SslMode}: credentials and score writes can be read or altered on the network. Use SslMode=VerifyFull (or VerifyCA).",
            host, string.IsNullOrEmpty(sslMode) ? "the driver default" : sslMode);
    }

    internal static bool TryFindRemoteHost(string connectionString, out string host, out string sslMode)
    {
        host = string.Empty;
        sslMode = string.Empty;

        var builder = new DbConnectionStringBuilder();
        try
        {
            builder.ConnectionString = connectionString;
        }
        catch (ArgumentException)
        {
            return false; // The driver reports a malformed connection string itself.
        }

        sslMode = Read(builder, "SslMode", "Ssl Mode", "Ssl-Mode");
        if (sslMode.Equals("VerifyFull", StringComparison.OrdinalIgnoreCase)
            || sslMode.Equals("VerifyCA", StringComparison.OrdinalIgnoreCase)
            || sslMode.Equals("Verify-Full", StringComparison.OrdinalIgnoreCase)
            || sslMode.Equals("Verify-CA", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var candidate in Read(builder, "Host", "Server", "Data Source").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = StripPort(candidate);
            if (name.Length == 0 || name.StartsWith('/') || IsLoopback(name))
            {
                continue; // Unix sockets and loopback never cross the network.
            }

            host = name;
            return true;
        }

        return false;
    }

    private static string Read(DbConnectionStringBuilder builder, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (builder.TryGetValue(key, out var value) && value?.ToString() is { Length: > 0 } text)
            {
                return text.Trim();
            }
        }

        return string.Empty;
    }

    private static string StripPort(string host)
    {
        if (host.StartsWith('['))
        {
            var end = host.IndexOf(']');
            return end > 0 ? host[1..end] : host;
        }

        // A single colon is host:port; several are a bare IPv6 address.
        var colon = host.IndexOf(':');
        return colon > 0 && colon == host.LastIndexOf(':') ? host[..colon] : host;
    }

    private static bool IsLoopback(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
           || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
}

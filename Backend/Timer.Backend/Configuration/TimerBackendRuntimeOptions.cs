using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using Microsoft.Extensions.Configuration;

namespace Timer.Backend.Configuration;

internal sealed record TimerBackendRuntimeOptions
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan WorkerShutdownTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan WorkerMaxLag { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Database-backed REST reads (cache misses and health probes) running at once; the rest queue.</summary>
    public int ReadConcurrencyLimit { get; init; } = 32;

    /// <summary>
    /// Reads waiting for a <see cref="ReadConcurrencyLimit"/> slot before further ones get 429. Generous: a cache
    /// expiry sends every concurrent reader of that board to the database at once, and waiting costs only a queue slot.
    /// </summary>
    public int ReadQueueLimit { get; init; } = 1024;

    /// <summary>Per-client-IP REST requests per second reaching the endpoints (cache hits excluded); 0 disables it.</summary>
    public int ClientRequestsPerSecond { get; init; }

    /// <summary>Requests a client may burst above <see cref="ClientRequestsPerSecond"/>.</summary>
    public int ClientRequestBurst { get; init; }

    /// <summary>Proxies whose X-Forwarded-For/-Proto is trusted; with none, forwarded headers are ignored.</summary>
    public IReadOnlyList<IPAddress> KnownProxies { get; init; } = [];

    public IReadOnlyList<IPNetwork> KnownProxyNetworks { get; init; } = [];

    /// <summary>Local ports serving /health/ready and /health/worker; empty serves them on every listener.</summary>
    public IReadOnlySet<int> InternalHealthPorts { get; init; } = new HashSet<int>();

    public bool TrustsForwardedHeaders => KnownProxies.Count != 0 || KnownProxyNetworks.Count != 0;

    public static TimerBackendRuntimeOptions FromConfiguration(IConfiguration configuration)
    {
        var perSecond = ReadInteger(configuration, "ClientRequestsPerSecond", 0, 0, 100_000);

        return new()
        {
            RequestTimeout = ReadSeconds(configuration, "RequestTimeoutSeconds", 15, 300),
            WorkerShutdownTimeout = ReadSeconds(configuration, "WorkerShutdownTimeoutSeconds", 15, 120),
            WorkerMaxLag = ReadSeconds(configuration, "WorkerMaxLagSeconds", 300, 86400),
            ReadConcurrencyLimit = ReadInteger(configuration, "ReadConcurrencyLimit", 32, 1, 10_000),
            ReadQueueLimit = ReadInteger(configuration, "ReadQueueLimit", 1024, 0, 100_000),
            ClientRequestsPerSecond = perSecond,
            ClientRequestBurst = ReadInteger(configuration, "ClientRequestBurst", perSecond * 2, 0, 1_000_000),
            KnownProxies = ReadList(configuration, "ForwardedHeaders:KnownProxies", static (raw, path) =>
                IPAddress.TryParse(raw, out var address)
                    ? address
                    : throw new InvalidOperationException($"{path} must be an IP address.")),
            KnownProxyNetworks = ReadList(configuration, "ForwardedHeaders:KnownNetworks", static (raw, path) =>
                IPNetwork.TryParse(raw, out var network)
                    ? network
                    : throw new InvalidOperationException($"{path} must be a CIDR network such as 10.0.0.0/8.")),
            InternalHealthPorts = ReadList(configuration, "InternalHealthPorts", static (raw, path) =>
                int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
                    ? port
                    : throw new InvalidOperationException($"{path} must be a port number from 1 through 65535."))
                .ToHashSet(),
        };
    }

    private static TimeSpan ReadSeconds(IConfiguration configuration, string name, int fallback, int maximum)
    {
        var path = $"TimerBackend:Runtime:{name}";
        var raw = configuration[path];
        if (raw is null) return TimeSpan.FromSeconds(fallback);
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value < 1 || value > maximum)
            throw new InvalidOperationException($"{path} must be an integer from 1 through {maximum}.");
        return TimeSpan.FromSeconds(value);
    }

    private static int ReadInteger(IConfiguration configuration, string name, int fallback, int minimum, int maximum)
    {
        var path = $"TimerBackend:Runtime:{name}";
        var raw = configuration[path];
        if (raw is null) return fallback;
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value < minimum || value > maximum)
            throw new InvalidOperationException($"{path} must be an integer from {minimum} through {maximum}.");
        return value;
    }

    // A scalar (environment variable) or a list, but not both, as WriteApi:LocalPorts.
    private static List<T> ReadList<T>(IConfiguration configuration, string name, Func<string, string, T> parse)
    {
        var section = configuration.GetSection($"TimerBackend:Runtime:{name}");
        var children = section.GetChildren().ToList();
        var values = new List<T>();
        if (!string.IsNullOrWhiteSpace(section.Value))
        {
            if (children.Count != 0)
            {
                throw new InvalidOperationException($"{section.Path} is set both as a single value and as a list. Use one form.");
            }

            values.Add(parse(section.Value.Trim(), section.Path));
        }

        foreach (var child in children)
        {
            values.Add(parse(child.Value?.Trim() ?? string.Empty, child.Path));
        }

        return values;
    }
}

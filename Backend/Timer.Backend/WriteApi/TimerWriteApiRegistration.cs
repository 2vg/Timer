using System;
using System.Collections.Generic;
using System.Net;
using MagicOnion.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Timer.Backend.Configuration;
using Timer.Backend.Storage;

namespace Timer.Backend.WriteApi;

/// <summary>
/// Keeps the MagicOnion write surface completely absent unless the explicit write-role switch
/// is enabled. The read-only REST host therefore retains its existing dependency graph and routes.
/// </summary>
internal static class TimerWriteApiRegistration
{
    internal const int MaxMessageSizeBytes = 64 * 1024;
    internal const int MaxResponseSizeBytes = 64 * 1024 * 1024;

    public static void Add(IServiceCollection services, TimerWriteApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return;
        }

        // Endpoint routing is not bound to a Kestrel listener, so without a port gate the write
        // service is reachable on every listener that speaks HTTP/2 (e.g. a TLS read port).
        if (options.LocalPorts.Count == 0 && !options.AllowAllListeners)
        {
            throw new InvalidOperationException(
                $"{TimerWriteApiOptions.SectionName}:LocalPorts is empty. Set it to the gRPC listener's port, or set {TimerWriteApiOptions.SectionName}:AllowAllListeners=true to serve write RPCs on every listener.");
        }

        // Deliberately do not widen TimerBackendStorage. This separate facade shares the
        // owner's lifecycle/scope while keeping REST-facing storage publicly read-only.
        services.AddSingleton<TimerBackendWriteStorage>();
        services.AddSingleton<TimerBackendGameStorage>();
        services.AddGrpc(grpc =>
        {
            // A legal v1 request contains at most 63 compact checkpoints and is only a
            // few KiB. Keep bounded headroom for future append-only fields without accepting
            // the 4 MiB framework default into the public deserialization path.
            grpc.MaxReceiveMessageSize = MaxMessageSizeBytes;
            // Responses are the backend's own data; a full map's leaderboards run to a few MiB.
            grpc.MaxSendMessageSize = MaxResponseSizeBytes;
            grpc.EnableDetailedErrors = false;

            grpc.Interceptors.Add<TimerWriteAccessInterceptor>();
        });
        services.AddMagicOnion(magicOnion =>
            magicOnion.MessageSerializer = new TimerWriteMessageSerializerProvider());
    }

    public static void Map(IEndpointRouteBuilder endpoints, TimerWriteApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Enabled)
        {
            endpoints.MapMagicOnionService([typeof(TimerWriteServiceV1), typeof(TimerStorageServiceV1)]);
        }
    }

    /// <summary>
    /// Warns, without refusing to start, when the configured listeners expose the write API beyond loopback in a
    /// way the operator may not intend: no credentials and no allow-list, or API keys sent in cleartext.
    /// </summary>
    public static void LogExposureWarnings(ILogger logger, IConfiguration configuration, TimerWriteApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return;
        }

        foreach (var url in ConfiguredListenerUrls(configuration))
        {
            BindingAddress address;
            try
            {
                address = BindingAddress.Parse(url);
            }
            catch (FormatException)
            {
                continue; // Kestrel reports malformed URLs itself.
            }

            if (address.IsUnixPipe || address.IsNamedPipe || IsLoopbackHost(address.Host)
                || !(options.AllowAllListeners || options.LocalPorts.Contains(address.Port)))
            {
                continue;
            }

            if (options.AuthenticationMode == TimerWriteAuthenticationMode.None && options.AllowedNetworks.Count == 0)
            {
                logger.LogWarning(
                    "The write API on {Url} accepts calls from any host without credentials. Firewall it to the game servers, or set {Networks} or {Mode}=ApiKey.",
                    url, $"{TimerWriteApiOptions.SectionName}:AllowedNetworks", $"{TimerWriteApiOptions.SectionName}:Authentication:Mode");
            }
            else if (options.AuthenticationMode == TimerWriteAuthenticationMode.ApiKey
                     && string.Equals(address.Scheme, "http", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "The write API on {Url} is plain HTTP/2: API keys and submissions cross the network unencrypted. Serve it over https.",
                    url);
            }
        }
    }

    private static IEnumerable<string> ConfiguredListenerUrls(IConfiguration configuration)
    {
        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(endpoint["Url"]))
            {
                yield return endpoint["Url"]!.Trim();
            }
        }

        foreach (var url in (configuration["urls"] ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return url;
        }
    }

    private static bool IsLoopbackHost(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
           || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
}

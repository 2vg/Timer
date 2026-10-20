using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Timer.Backend.Configuration;

namespace Timer.Backend.WriteApi;

/// <summary>
/// Gates every write RPC, cheapest check first: the accepted connection's local port (never the client-controlled
/// Host/:authority header), the peer network, then the API key and the method's scope. Allocation-free for accepted
/// calls apart from what Kestrel already materialised.
/// </summary>
internal sealed class TimerWriteAccessInterceptor : Interceptor
{
    private const string BearerPrefix = "Bearer ";

    private readonly IReadOnlySet<int>                                     _localPorts;
    private readonly bool                                                  _allowAllListeners;
    private readonly IPNetwork[]                                           _allowedNetworks;
    private readonly TimerWriteAuthenticationMode                          _mode;
    private readonly FrozenDictionary<TimerWriteKeyHash, TimerWriteClient> _clients;
    private readonly ILogger                                               _logger;

    public TimerWriteAccessInterceptor(TimerWriteApiOptions options, ILogger<TimerWriteAccessInterceptor> logger)
    {
        _localPorts        = options.LocalPorts;
        _allowAllListeners = options.AllowAllListeners;
        _allowedNetworks   = [.. options.AllowedNetworks];
        _mode              = options.AuthenticationMode;
        _clients           = options.Clients;
        _logger            = logger;
    }

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        return continuation(request, context);
    }

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        return continuation(requestStream, context);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        return continuation(request, responseStream, context);
    }

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        return continuation(requestStream, responseStream, context);
    }

    private void Authorize(ServerCallContext context)
    {
        var http = context.GetHttpContext();
        var connection = http.Connection;

        if (!_allowAllListeners && !_localPorts.Contains(connection.LocalPort))
        {
            throw TimerWriteRpcErrors.NotServedOnListener();
        }

        if (_allowedNetworks.Length != 0 && !IsAllowedPeer(connection.RemoteIpAddress))
        {
            throw TimerWriteRpcErrors.PermissionDenied();
        }

        if (_mode == TimerWriteAuthenticationMode.None)
        {
            return;
        }

        var client = Authenticate(http.Request.Headers.Authorization);
        if (client is null)
        {
            throw TimerWriteRpcErrors.Unauthenticated();
        }

        var required = TimerWriteMethodScopes.Required(context.Method);
        if ((client.Scopes & required) != required)
        {
            _logger.LogWarning("Write API client {Client} was denied {Method}: it lacks the {Scope} scope.",
                               client.Name, context.Method, required);
            throw TimerWriteRpcErrors.PermissionDenied();
        }

        // Lets a service narrow what a Game-only caller may change within a Game method.
        http.Features.Set(client);

        if (required == TimerWriteScopes.Admin)
        {
            _logger.LogInformation("Write API client {Client} from {Peer} called {Method}.",
                                   client.Name, connection.RemoteIpAddress, context.Method);
        }
    }

    private TimerWriteClient? Authenticate(Microsoft.Extensions.Primitives.StringValues header)
    {
        // Exactly one Authorization value; several are ambiguous and refused.
        if (header.Count != 1 || header[0] is not { } value
            || !value.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return TimerWriteKeyHash.TryCompute(value.AsSpan(BearerPrefix.Length).Trim(), out var hash)
               && _clients.TryGetValue(hash, out var client)
                   ? client
                   : null;
    }

    private bool IsAllowedPeer(IPAddress? address)
    {
        // No peer address (Unix socket, named pipe) cannot match a network allow-list.
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        foreach (var network in _allowedNetworks)
        {
            if (network.Contains(address))
            {
                return true;
            }
        }

        return false;
    }
}

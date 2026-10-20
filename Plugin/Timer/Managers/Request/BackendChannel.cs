/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo and Kxnrl
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using MagicOnion;
using MagicOnion.Client;
using Source2Surf.Timer.Configuration;

namespace Source2Surf.Timer.Managers.Request;

/// <summary>
/// The one gRPC channel to Timer.Backend, shared by run submissions and every other request. It connects on the
/// first call and reconnects by itself.
/// </summary>
internal sealed class BackendChannel : IDisposable
{
    // A whole map's leaderboards can run to a few MiB.
    private const int MaxResponseBytes = 64 * 1024 * 1024;

    private readonly GrpcChannel _channel;

    public BackendChannel(BackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Endpoint.Scheme == Uri.UriSchemeHttp)
        {
            // MagicOnion pins a Grpc.Net.Client target without per-channel HTTP-version properties for this TFM.
            // The documented h2c switch is global.
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        }

        // A map start sends every player's lookups at once. Past the server's per-connection stream limit
        // (100 on Kestrel), open another connection instead of queueing run submissions behind them.
        HttpMessageHandler handler = new SocketsHttpHandler { EnableMultipleHttp2Connections = true };
        if (options.ApiKey is { } apiKey)
        {
            handler = new ApiKeyHandler(apiKey, handler);
        }

        _channel = GrpcChannel.ForAddress(options.Endpoint, new GrpcChannelOptions
        {
            MaxReceiveMessageSize = MaxResponseBytes,
            HttpHandler           = handler,
        });
    }

    public T CreateClient<T>() where T : IService<T>
        => MagicOnionClient.Create<T>(_channel);

    public void Dispose()
        => _channel.Dispose();

    // A header on the HTTP request rather than gRPC CallCredentials, which Grpc.Net.Client refuses on h2c channels.
    private sealed class ApiKeyHandler(string apiKey, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly AuthenticationHeaderValue _authorization = new ("Bearer", apiKey);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = _authorization;
            return base.SendAsync(request, cancellationToken);
        }
    }
}

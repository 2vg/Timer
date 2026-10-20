using System.Collections.Generic;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Timer.Backend.Configuration;

namespace Timer.Backend.WriteApi;

/// <summary>
/// Serves write RPCs only on the configured local listener ports. The check uses the accepted
/// connection's local port rather than the Host/:authority header, which a client controls.
/// </summary>
internal sealed class TimerWriteListenerInterceptor : Interceptor
{
    private readonly IReadOnlySet<int> _localPorts;

    public TimerWriteListenerInterceptor(TimerWriteApiOptions options)
        => _localPorts = options.LocalPorts;

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        EnsureWriteListener(context);
        return continuation(request, context);
    }

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        EnsureWriteListener(context);
        return continuation(requestStream, context);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        EnsureWriteListener(context);
        return continuation(request, responseStream, context);
    }

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        EnsureWriteListener(context);
        return continuation(requestStream, responseStream, context);
    }

    private void EnsureWriteListener(ServerCallContext context)
    {
        if (!_localPorts.Contains(context.GetHttpContext().Connection.LocalPort))
        {
            throw TimerWriteRpcErrors.NotServedOnListener();
        }
    }
}

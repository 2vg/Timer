using System.Buffers;
using System.Reflection;
using Grpc.Core;
using MagicOnion.Serialization;
using MagicOnion.Serialization.MessagePack;
using MessagePack;

namespace Timer.Backend.WriteApi;

/// <summary>
/// MagicOnion decodes request messages in the gRPC marshaller, before any interceptor or service
/// code runs. A malformed payload therefore surfaced as an unhandled exception: status Unknown
/// plus an error-level stack trace per call, which any unauthenticated client could use to flood
/// the log. Report it as InvalidArgument (an RpcException is logged as an expected status).
/// </summary>
internal sealed class TimerWriteMessageSerializerProvider : IMagicOnionSerializerProvider
{
    private readonly IMagicOnionSerializerProvider _inner;

    // Requests come from the network: UntrustedData bounds nesting depth and builds dictionaries with
    // collision-resistant comparers. It only affects deserialization, never the responses' encoding.
    public TimerWriteMessageSerializerProvider(IMagicOnionSerializerProvider? inner = null)
        => _inner = inner ?? MessagePackMagicOnionSerializerProvider.Default.WithOptions(
               MessagePackSerializer.DefaultOptions.WithSecurity(MessagePackSecurity.UntrustedData));

    public IMagicOnionSerializer Create(MethodType methodType, MethodInfo? methodInfo)
        => new InvalidArgumentOnMalformedPayload(_inner.Create(methodType, methodInfo));

    private sealed class InvalidArgumentOnMalformedPayload(IMagicOnionSerializer inner) : IMagicOnionSerializer
    {
        public void Serialize<T>(IBufferWriter<byte> writer, in T? value)
            => inner.Serialize(writer, value);

        public T Deserialize<T>(in ReadOnlySequence<byte> bytes)
        {
            try
            {
                return inner.Deserialize<T>(bytes);
            }
            catch (MessagePackSerializationException)
            {
                throw TimerWriteRpcErrors.InvalidArgument();
            }
        }
    }
}

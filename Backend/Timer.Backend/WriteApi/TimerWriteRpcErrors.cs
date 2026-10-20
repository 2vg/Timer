using System;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Timer.Backend.Storage;

namespace Timer.Backend.WriteApi;

internal static class TimerWriteRpcErrors
{
    private const string InvalidArgumentMessage = "The request is invalid.";
    private const string PolicyFailureMessage = "The requested write policy is not available.";
    private const string NotFoundMessage = "The requested resource does not exist.";
    private const string ConflictMessage = "The submission conflicts with a prior request.";
    private const string InternalFailureMessage = "The request could not be completed.";
    private const string UnavailableMessage = "The timer database is unavailable.";
    private const string NotServedOnListenerMessage = "The write API is not served on this listener.";

    public static RpcException InvalidArgument()
        => new (new Status(StatusCode.InvalidArgument, InvalidArgumentMessage));

    public static RpcException RulesetMismatch()
        => new (new Status(StatusCode.FailedPrecondition, PolicyFailureMessage));

    public static RpcException StyleDisabled()
        => new (new Status(StatusCode.FailedPrecondition, PolicyFailureMessage));

    public static RpcException ScorePolicyNotConfigured()
        => new (new Status(StatusCode.FailedPrecondition,
                           "Tier changes and score recalculation need style factors: start a game server once so it registers its styles, or configure TimerBackend:WriteApi:StyleFactors."));

    public static RpcException InternalMappingFailure()
        => new (new Status(StatusCode.Internal, InternalFailureMessage));

    // Same status a client sees when the write API is disabled: the service is absent here.
    public static RpcException NotServedOnListener()
        => new (new Status(StatusCode.Unimplemented, NotServedOnListenerMessage));

    public static RpcException Unauthenticated()
        => new (new Status(StatusCode.Unauthenticated, "The write API requires a valid API key."));

    public static RpcException PermissionDenied()
        => new (new Status(StatusCode.PermissionDenied, "The caller may not perform this operation."));

    public static RpcException Translate(Exception exception, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(logger);

        if (exception is RpcException rpcException)
        {
            // Never collapse an already-classified status, particularly cancellation/deadline.
            return rpcException;
        }

        return exception switch
        {
            TimerBackendSubmissionValidationException => InvalidArgument(),
            TimerBackendPlayerProfileValidationException => InvalidArgument(),
            TimerBackendMapNotFoundException => new RpcException(new Status(StatusCode.NotFound, NotFoundMessage)),
            TimerBackendPlayerNotFoundException => new RpcException(new Status(StatusCode.NotFound, NotFoundMessage)),
            TimerBackendSubmissionConflictException => new RpcException(new Status(StatusCode.AlreadyExists, ConflictMessage)),
            TimerBackendSubmissionPolicyException => RulesetMismatch(),
            TimerBackendUnavailableException => LogAndUnavailable(exception, logger),
            // Storage's own range checks on client-supplied values: a client error, so no Error log per call.
            ArgumentException => LogAndInvalidArgument(exception, logger),
            _ => LogAndInternal(exception, logger),
        };
    }

    public static RpcException CancellationOrDeadline(DateTime deadlineUtc)
        => deadlineUtc != DateTime.MaxValue && deadlineUtc <= DateTime.UtcNow
            ? new RpcException(new Status(StatusCode.DeadlineExceeded, "The request deadline elapsed."))
            : new RpcException(new Status(StatusCode.Cancelled, "The request was cancelled."));

    private static RpcException LogAndUnavailable(Exception exception, ILogger logger)
    {
        logger.LogWarning(exception, "Timer write RPC failed because the database is unavailable.");
        return new RpcException(new Status(StatusCode.Unavailable, UnavailableMessage));
    }

    private static RpcException LogAndInvalidArgument(Exception exception, ILogger logger)
    {
        logger.LogDebug(exception, "Timer write RPC rejected an out-of-range argument.");
        return InvalidArgument();
    }

    private static RpcException LogAndInternal(Exception exception, ILogger logger)
    {
        logger.LogError(exception, "Unhandled Timer write RPC failure.");
        return new RpcException(new Status(StatusCode.Internal, InternalFailureMessage));
    }
}

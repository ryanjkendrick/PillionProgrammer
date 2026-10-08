using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Pillion.Contracts;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Pillion.Host.Rpc;

/// <summary>
/// The server's <see cref="JsonRpc"/>: refuses every call except the handshake until the handshake is accepted, maps
/// exceptions to Pillion errors, and tracks in-flight requests so a graceful stop can let them finish.
/// </summary>
/// <remarks>
/// Error mapping (what a client sees):
/// <list type="bullet">
/// <item><see cref="PillionRpcException"/>: JSON-RPC code <see cref="JsonRpcErrorCode.InvocationError"/> (-32000),
/// message <see cref="PillionError.Message"/>, <c>data</c> the <see cref="PillionError"/>.</item>
/// <item><see cref="OperationCanceledException"/>: code <see cref="JsonRpcErrorCode.RequestCanceled"/> (-32800),
/// <c>data</c> a <see cref="PillionError"/> with <see cref="PillionErrorCode.Cancelled"/>.</item>
/// <item>Anything else: code -32000 with a generic message carrying a reference that appears in the host log, and
/// no <c>data</c>; the exception and stack trace are only logged on the host.</item>
/// </list>
/// </remarks>
internal sealed partial class PillionJsonRpc : JsonRpc
{
    /// <summary>The only method allowed before the handshake is accepted.</summary>
    public const string HandshakeMethodName = nameof(IHostLifecycleService.HandshakeAsync);

    private readonly IJsonRpcMessageFactory? _messageFactory;
    private readonly Func<bool> _isHandshakeAccepted;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<RequestId, byte> _awaitingResponse = new();
    private int _dispatching;

    /// <summary>Initializes a new instance of the <see cref="PillionJsonRpc"/> class.</summary>
    /// <param name="handler">The message handler (length-prefixed MessagePack over the pipe).</param>
    /// <param name="isHandshakeAccepted">Returns whether the connection's handshake has been accepted.</param>
    /// <param name="logger">The logger.</param>
    public PillionJsonRpc(IJsonRpcMessageHandler handler, Func<bool> isHandshakeAccepted, ILogger logger)
        : base(handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _messageFactory = handler.Formatter as IJsonRpcMessageFactory;
        _isHandshakeAccepted = isHandshakeAccepted;
        _logger = logger;
    }

    /// <summary>
    /// Raised after a response (result or error) has been written to the transport and the write has completed.
    /// </summary>
    /// <remarks>
    /// Not StreamJsonRpc's own <c>ResponseSent</c> event (<c>JsonRpc.OnResponseSent</c>): that one is raised when the
    /// response is serialised, before it is written, so closing the connection from it can cut the response off.
    /// </remarks>
    public event EventHandler<JsonRpcMessage>? ResponseSent;

    /// <summary>Gets a value indicating whether no request is being dispatched or waiting for its response to be sent.</summary>
    public bool IsIdle => Volatile.Read(ref _dispatching) == 0 && _awaitingResponse.IsEmpty;

    /// <inheritdoc/>
    protected override async ValueTask<JsonRpcMessage> DispatchRequestAsync(
        JsonRpcRequest request,
        TargetMethod targetMethod,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_isHandshakeAccepted() && !string.Equals(request.Method, HandshakeMethodName, StringComparison.Ordinal))
        {
            LogHandshakeRequired(_logger, request.Method);
            var denied = _messageFactory?.CreateErrorMessage() ?? new JsonRpcError();
            denied.RequestId = request.RequestId;
            denied.Error = ToErrorDetail(RpcErrors.HandshakeRequired().Error);
            return denied;
        }

        Interlocked.Increment(ref _dispatching);
        if (request.IsResponseExpected)
        {
            _awaitingResponse.TryAdd(request.RequestId, 0);
        }

        try
        {
            var response = await base.DispatchRequestAsync(request, targetMethod, cancellationToken).ConfigureAwait(false);

            // StreamJsonRpc builds the cancellation response itself when the client cancelled; give it our data too.
            if (response is JsonRpcError { Error: { Code: JsonRpcErrorCode.RequestCanceled, Data: null } detail })
            {
                detail.Data = RpcErrors.Cancelled();
            }

            return response;
        }
        catch
        {
            _awaitingResponse.TryRemove(request.RequestId, out _);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _dispatching);
        }
    }

    /// <inheritdoc/>
    protected override async ValueTask SendAsync(JsonRpcMessage message, CancellationToken cancellationToken)
    {
        // The base call starts the write before its first yield, which keeps outgoing messages in order.
        var isResponse = message is JsonRpcResult or JsonRpcError;
        var delivered = false;
        try
        {
            await base.SendAsync(message, cancellationToken).ConfigureAwait(false);
            delivered = true;
        }
        finally
        {
            if (isResponse && message is IJsonRpcMessageWithId { RequestId: var id })
            {
                _awaitingResponse.TryRemove(id, out _);
            }
        }

        if (delivered && isResponse)
        {
            ResponseSent?.Invoke(this, message);
        }
    }

    /// <inheritdoc/>
    protected override JsonRpcError.ErrorDetail CreateErrorDetails(JsonRpcRequest request, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actual = Unwrap(exception);
        switch (actual)
        {
            case PillionRpcException pillion:
                LogPillionError(_logger, request.Method, pillion.Error.Code);
                return ToErrorDetail(pillion.Error);

            case OperationCanceledException:
                LogCancelled(_logger, request.Method);
                return new JsonRpcError.ErrorDetail
                {
                    Code = JsonRpcErrorCode.RequestCanceled,
                    Message = RpcErrors.CancelledMessage,
                    Data = RpcErrors.Cancelled(),
                };

            default:
                var reference = Guid.NewGuid().ToString("N")[..12];
                LogUnexpected(_logger, actual, request.Method, reference);
                return new JsonRpcError.ErrorDetail
                {
                    Code = JsonRpcErrorCode.InvocationError,
                    Message = $"An unexpected error occurred in Pillion Host (reference {reference}). See the host log for details.",
                    Data = null,
                };
        }
    }

    private static JsonRpcError.ErrorDetail ToErrorDetail(PillionError error) => new()
    {
        Code = JsonRpcErrorCode.InvocationError,
        Message = error.Message,
        Data = new PillionError
        {
            Code = error.Code,
            Message = error.Message,
            RetryHint = error.RetryHint,
            Details = error.Details,
        },
    };

    private static Exception Unwrap(Exception exception)
    {
        while (true)
        {
            switch (exception)
            {
                case TargetInvocationException { InnerException: { } inner }:
                    exception = inner;
                    continue;
                case AggregateException aggregate when aggregate.InnerExceptions.Count == 1:
                    exception = aggregate.InnerExceptions[0];
                    continue;
                default:
                    return exception;
            }
        }
    }

    [LoggerMessage(EventId = 230, Level = LogLevel.Warning, Message = "Refused {Method}: the handshake has not been accepted.")]
    private static partial void LogHandshakeRequired(ILogger logger, string? method);

    [LoggerMessage(EventId = 231, Level = LogLevel.Information, Message = "{Method} failed with {Code}.")]
    private static partial void LogPillionError(ILogger logger, string? method, PillionErrorCode code);

    [LoggerMessage(EventId = 232, Level = LogLevel.Debug, Message = "{Method} was cancelled.")]
    private static partial void LogCancelled(ILogger logger, string? method);

    [LoggerMessage(EventId = 233, Level = LogLevel.Error, Message = "{Method} failed unexpectedly (reference {Reference}).")]
    private static partial void LogUnexpected(ILogger logger, Exception exception, string? method, string reference);
}

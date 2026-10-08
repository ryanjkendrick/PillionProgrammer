using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace Pillion.Host.Rpc;

/// <summary>
/// Forwards StreamJsonRpc's <see cref="TraceSource"/> output to an <see cref="ILogger"/>. Only warnings and errors are
/// traced (see <see cref="CreateTraceSource"/>), which never include message payloads. Local invocation errors are
/// demoted to Debug because <see cref="PillionJsonRpc"/> already logs them.
/// </summary>
internal sealed partial class JsonRpcTraceListener : TraceListener
{
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="JsonRpcTraceListener"/> class.</summary>
    /// <param name="logger">The logger to forward to.</param>
    public JsonRpcTraceListener(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Creates a trace source for a <see cref="StreamJsonRpc.JsonRpc"/> that logs warnings and errors.</summary>
    /// <param name="logger">The logger to forward to.</param>
    /// <returns>The trace source.</returns>
    public static TraceSource CreateTraceSource(ILogger logger)
    {
        var source = new TraceSource("StreamJsonRpc", SourceLevels.Warning);
        source.Listeners.Clear();
        source.Listeners.Add(new JsonRpcTraceListener(logger));
        return source;
    }

    /// <inheritdoc/>
    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message) =>
        Forward(eventType, id, message);

    /// <inheritdoc/>
    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args) =>
        Forward(eventType, id, args is { Length: > 0 } && format is not null ? string.Format(CultureInfo.InvariantCulture, format, args) : format);

    /// <inheritdoc/>
    public override void TraceData(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, object? data) =>
        Forward(eventType, id, data?.ToString());

    /// <inheritdoc/>
    public override void TraceData(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, params object?[]? data) =>
        Forward(eventType, id, data is null ? null : string.Join(", ", data));

    /// <inheritdoc/>
    public override void Write(string? message) => Forward(TraceEventType.Verbose, 0, message);

    /// <inheritdoc/>
    public override void WriteLine(string? message) => Forward(TraceEventType.Verbose, 0, message);

    private void Forward(TraceEventType eventType, int id, string? message)
    {
        // Exceptions thrown by RPC targets are logged once, at the right level, by PillionJsonRpc.CreateErrorDetails;
        // StreamJsonRpc's own copy (with stack trace, traced twice) is only wanted when debugging.
        var level = id == (int)JsonRpc.TraceEvents.LocalInvocationError ? LogLevel.Debug : eventType switch
        {
            TraceEventType.Critical => LogLevel.Critical,
            TraceEventType.Error => LogLevel.Error,
            TraceEventType.Warning => LogLevel.Warning,
            TraceEventType.Information => LogLevel.Debug,
            _ => LogLevel.Trace,
        };

        if (string.IsNullOrEmpty(message) || !_logger.IsEnabled(level))
        {
            return;
        }

        LogTrace(_logger, level, id, message);
    }

    [LoggerMessage(EventId = 240, Message = "StreamJsonRpc trace {TraceId}: {TraceMessage}")]
    private static partial void LogTrace(ILogger logger, LogLevel level, int traceId, string traceMessage);
}

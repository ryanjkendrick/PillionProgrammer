namespace Pillion.ModelGateway;

/// <summary>What a progress notice is about.</summary>
public enum ModelProgressKind
{
    /// <summary>A local model has not produced its first token yet (it may be loading).</summary>
    WaitingForLocalModel,

    /// <summary>A local model has exceeded its first-token budget; the request continues.</summary>
    SlowerThanExpected,

    /// <summary>The request is being retried after a transient provider error.</summary>
    Retrying,

    /// <summary>The request moved to the next model in its fallback chain.</summary>
    FallingBack,
}

/// <summary>A progress notice for a request in flight (never contains prompt text).</summary>
/// <param name="RequestId">The request id.</param>
/// <param name="ProviderId">The provider.</param>
/// <param name="ModelId">The model.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Elapsed">Time since the request was sent.</param>
/// <param name="Message">A user-facing description.</param>
public sealed record ModelProgress(string RequestId, string ProviderId, string ModelId, ModelProgressKind Kind, TimeSpan Elapsed, string Message);

/// <summary>
/// Receives progress notices, so a slow local model shows progress instead of failing (NF1). The Chat window wires a
/// real sink later; <see cref="NoProgressSink"/> drops them.
/// </summary>
public interface IProgressSink
{
    /// <summary>Reports progress.</summary>
    /// <param name="progress">The notice.</param>
    void Report(ModelProgress progress);
}

/// <summary>An <see cref="IProgressSink"/> that does nothing.</summary>
public sealed class NoProgressSink : IProgressSink
{
    /// <inheritdoc/>
    public void Report(ModelProgress progress)
    {
    }
}

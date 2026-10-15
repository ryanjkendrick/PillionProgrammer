namespace Pillion.ModelGateway;

/// <summary>Tunable limits of the gateway. The defaults are the values in design sections 6 and 13.</summary>
public sealed class ModelGatewayOptions
{
    /// <summary>Gets or sets the hard limit for a cloud autocomplete request; on expiry the suggestion is dropped silently. Default 3 s.</summary>
    public TimeSpan CloudAutocompleteTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Gets or sets the time a cloud chat or agent request may take to produce its first token. Default 60 s.</summary>
    public TimeSpan CloudFirstTokenTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets the baseline time to first token for a local model, before scaling by prompt size. A local request
    /// that takes longer is reported as progress, never failed. Default 120 s.
    /// </summary>
    public TimeSpan LocalFirstTokenBaseline { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>Gets or sets the prompt size, in tokens, that the local baseline covers; larger prompts scale the budget linearly. Default 4,096.</summary>
    public int LocalBaselinePromptTokens { get; set; } = 4096;

    /// <summary>
    /// Gets or sets the smallest first-token budget of a local autocomplete request. The budget is three times the prompt
    /// size divided by the model's measured prompt-evaluation rate, but never less than this; past it the request reports
    /// that it is slow and keeps waiting (a cold load shows progress rather than failing). Default 10 s.
    /// </summary>
    public TimeSpan LocalAutocompleteFirstTokenFloor { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets how long a local request waits for its first token before a "waiting for the local model" progress notice. Default 2 s.</summary>
    public TimeSpan LocalProgressDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets or sets how many times a request is retried on HTTP 429 or 5xx (autocomplete is never retried). Default 2.</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Gets or sets the base delay of the exponential backoff between retries. Default 500 ms.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Gets or sets the longest delay between retries. Default 8 s.</summary>
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>Gets or sets the number of consecutive failures that opens a provider's circuit. Default 3.</summary>
    public int CircuitFailureThreshold { get; set; } = 3;

    /// <summary>Gets or sets how long an open circuit refuses requests. Default 60 s.</summary>
    public TimeSpan CircuitOpenDuration { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets or sets the autocomplete requests in flight per provider. Default 1.</summary>
    public int AutocompleteConcurrency { get; set; } = 1;

    /// <summary>Gets or sets the chat requests in flight per provider. Default 2.</summary>
    public int ChatConcurrency { get; set; } = 2;

    /// <summary>Gets or sets the agent requests in flight per provider. Default 1.</summary>
    public int AgentConcurrency { get; set; } = 1;

    /// <summary>Gets or sets the number of texts sent to a provider in one embedding request. Default 32 (design section 7, Embed).</summary>
    public int EmbeddingBatchSize { get; set; } = 32;

    /// <summary>
    /// Gets or sets how many embedding batches in a row may fail before the rest of the call is skipped, so a provider that is
    /// down is not asked a thousand times. 0 never skips. Default 3, the circuit breaker's threshold.
    /// </summary>
    public int EmbeddingMaxConsecutiveFailures { get; set; } = 3;

    /// <summary>Gets or sets the embedding batches in flight per provider, in a lane of their own so that indexing cannot crowd out chat. Default 1.</summary>
    public int EmbeddingConcurrency { get; set; } = 1;

    /// <summary>Gets or sets the number of entries in the autocomplete cache. Default 256.</summary>
    public int AutocompleteCacheCapacity { get; set; } = 256;

    /// <summary>Gets or sets the number of records the in-memory Outbound log keeps. Default 500.</summary>
    public int OutboundLogCapacity { get; set; } = 500;

    /// <summary>Gets or sets how long listing one provider's models may take before it is reported unavailable. Default 10 s.</summary>
    public TimeSpan ListModelsTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the sampling temperature sent with inline edit requests (other chat requests use the provider's
    /// default). Default 0.2: an edit should follow the instruction closely and copy the old lines exactly.
    /// </summary>
    public float InlineEditTemperature { get; set; } = 0.2f;

    /// <summary>Gets the first-token budget for a local request of the given size.</summary>
    /// <param name="promptTokens">The estimated prompt tokens.</param>
    /// <returns>The baseline, scaled up linearly for prompts larger than <see cref="LocalBaselinePromptTokens"/>.</returns>
    public TimeSpan LocalFirstTokenBudget(int promptTokens)
    {
        var scale = Math.Max(1.0, promptTokens / (double)Math.Max(1, LocalBaselinePromptTokens));
        return TimeSpan.FromTicks((long)(LocalFirstTokenBaseline.Ticks * scale));
    }
}

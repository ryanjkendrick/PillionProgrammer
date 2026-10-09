using Pillion.Contracts;

namespace Pillion.ModelGateway;

/// <summary>
/// Fill-in-the-middle (FIM) code completion, which the Microsoft.Extensions.AI chat abstraction does not model.
/// One client serves one model.
/// </summary>
public interface IFimClient
{
    /// <summary>Streams the text that belongs between <see cref="FimRequest.Prefix"/> and <see cref="FimRequest.Suffix"/>.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">A token that cancels the stream.</param>
    /// <returns>The generated text, in pieces, in order.</returns>
    /// <exception cref="ProviderException">The provider failed; the exception says whether it is worth retrying.</exception>
    IAsyncEnumerable<string> CompleteAsync(FimRequest request, CancellationToken cancellationToken);
}

/// <summary>A fill-in-the-middle request as the gateway hands it to an <see cref="IFimClient"/>.</summary>
public sealed record FimRequest
{
    /// <summary>Gets the text before the caret.</summary>
    public string Prefix { get; init; } = string.Empty;

    /// <summary>Gets the text after the caret.</summary>
    public string Suffix { get; init; } = string.Empty;

    /// <summary>Gets the most tokens to generate, or 0 for the provider's default.</summary>
    public int MaxTokens { get; init; }

    /// <summary>Gets sequences that end the completion when generated.</summary>
    public IReadOnlyList<string> StopSequences { get; init; } = [];

    /// <summary>Gets the sampling temperature, or <see langword="null"/> for the provider's default.</summary>
    public float? Temperature { get; init; }

    /// <summary>Gets an explicit model id that overrides routing, or <see langword="null"/> to route by profile.</summary>
    public string? ModelId { get; init; }

    /// <summary>Gets the profile the gateway routed the request under; providers use it for per-profile options such as the context size.</summary>
    public Profile? Profile { get; init; }

    /// <summary>Gets the context size to request from a local runtime, or <see langword="null"/> for the provider's per-profile default.</summary>
    public int? ContextSize { get; init; }

    /// <summary>Gets the id of the gateway request, so a local runtime can attach its load and unload progress to it.</summary>
    public string? RequestId { get; init; }
}

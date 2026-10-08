using Microsoft.Extensions.AI;

namespace Pillion.ModelGateway;

/// <summary>
/// Keys the gateway puts in <see cref="ChatOptions.AdditionalProperties"/> for provider adapters. Adapters that do not
/// understand a key ignore it.
/// </summary>
public static class GatewayOptionKeys
{
    /// <summary>The <see cref="Pillion.Contracts.Profile"/> the request was routed under (a local runtime picks its context size from it).</summary>
    public const string Profile = "pillion.profile";

    /// <summary>An explicit context size in tokens for a local runtime (Ollama's <c>num_ctx</c>), overriding the per-profile default.</summary>
    public const string ContextSize = "pillion.contextSize";

    /// <summary>An explicit residency for a local runtime (Ollama's <c>keep_alive</c>, for example <c>30m</c> or <c>-1</c>).</summary>
    public const string KeepAlive = "pillion.keepAlive";
}

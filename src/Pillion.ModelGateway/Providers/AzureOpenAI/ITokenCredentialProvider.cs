namespace Pillion.ModelGateway.Providers.AzureOpenAI;

/// <summary>
/// Supplies Microsoft Entra ID access tokens to the Azure OpenAI adapter, so a key is not needed. The adapter has no
/// dependency on <c>Azure.Identity</c>: Pillion Host implements this interface over a <c>TokenCredential</c> (for example
/// <c>DefaultAzureCredential</c>) and registers it in dependency injection. The adapter asks for a token for every
/// request, so an implementation must cache and refresh tokens itself (<c>TokenCredential</c> implementations do).
/// </summary>
public interface ITokenCredentialProvider
{
    /// <summary>Gets an access token for a scope.</summary>
    /// <param name="scope">The scope, for example <c>https://cognitiveservices.azure.com/.default</c>.</param>
    /// <param name="cancellationToken">A token that cancels the call.</param>
    /// <returns>The bearer token. It is sent in an <c>Authorization</c> header and never logged or stored by the adapter.</returns>
    /// <exception cref="InvalidOperationException">No credential is available or sign-in failed; the message is shown to the user and must not contain secrets.</exception>
    ValueTask<string> GetTokenAsync(string scope, CancellationToken cancellationToken);
}

/// <summary>An <see cref="ITokenCredentialProvider"/> that returns one fixed token, for tests and tools.</summary>
public sealed class StaticTokenCredentialProvider : ITokenCredentialProvider
{
    private readonly string _token;
    private readonly List<string> _requested = [];

    /// <summary>Initializes a new instance of the <see cref="StaticTokenCredentialProvider"/> class.</summary>
    /// <param name="token">The token returned for every scope.</param>
    public StaticTokenCredentialProvider(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        _token = token;
    }

    /// <summary>Gets the scopes that were requested, in order.</summary>
    public IReadOnlyList<string> RequestedScopes
    {
        get
        {
            lock (_requested)
            {
                return [.. _requested];
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask<string> GetTokenAsync(string scope, CancellationToken cancellationToken)
    {
        lock (_requested)
        {
            _requested.Add(scope);
        }

        return new ValueTask<string>(_token);
    }
}

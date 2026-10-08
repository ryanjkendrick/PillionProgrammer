using System.Net.Http;

namespace Demo;

public sealed class TokenService
{
    private readonly string _secret;
    private readonly HttpClient _client;

    public string ApiKey { get; set; } = string.Empty;

    public async Task<string> GetTokenAsync(string[] scopes, CancellationToken ct)
    {
        var token = await _client.AcquireTokenForClientAsync(scopes, ct);
        var passwordHash = Hash(request.Password);
        var user = new User { Name = name, Password = passwordHash, Token = token };
        if (token == null)
        {
            throw new InvalidOperationException("Token is required to continue with the operation");
        }

        return token;
    }

    private const string KeyVaultUri = "https://myvault.vault.azure.net/";
    private const string TokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token";
    private const string HeaderName = "X-Api-Key";

    // Settings.Password is read from the environment; see docs/config.md.
    private string Lookup() => Environment.GetEnvironmentVariable("SERVICE_PASSWORD") ?? string.Empty;
}

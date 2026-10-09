namespace Pillion.ModelGateway.Tests.Providers.Cloud;

/// <summary>
/// A fact that runs only when every named environment variable is set, so a live test never reads a key from anywhere
/// else on the machine. Live tests are marked <c>[Trait("Category", "Live")]</c> on their class.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LiveFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="LiveFactAttribute"/> class.</summary>
    /// <param name="variables">The environment variables that must be set.</param>
    public LiveFactAttribute(params string[] variables)
    {
        var missing = variables.Where(v => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v))).ToList();
        if (missing.Count > 0)
        {
            Skip = $"Set {string.Join(" and ", missing)} to run this live test.";
        }
    }
}

/// <summary>The environment variables of the cloud live tests.</summary>
internal static class LiveEnvironment
{
    public const string AnthropicKey = "PILLION_TEST_ANTHROPIC_KEY";

    public const string GeminiKey = "PILLION_TEST_GEMINI_KEY";

    public const string MistralKey = "PILLION_TEST_MISTRAL_KEY";

    public const string AzureEndpoint = "PILLION_TEST_AZURE_OPENAI_ENDPOINT";

    public const string AzureKey = "PILLION_TEST_AZURE_OPENAI_KEY";

    public const string AzureDeployment = "PILLION_TEST_AZURE_OPENAI_DEPLOYMENT";

    public static string Get(string variable) =>
        Environment.GetEnvironmentVariable(variable) ?? throw new InvalidOperationException($"{variable} is not set.");
}

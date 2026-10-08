using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Pillion.Common.Settings;

/// <summary>
/// Restrictions a repository places on Pillion through <c>.pillion/policy.json</c>. A policy can only make
/// Pillion more restrictive; it can never enable anything. Keys other than the ones modelled here are ignored.
/// </summary>
public sealed partial class RepositoryPolicy
{
    /// <summary>The path of the policy file relative to a repository root.</summary>
    public const string RelativePath = ".pillion/policy.json";

    /// <summary>Gets an empty policy that restricts nothing.</summary>
    public static RepositoryPolicy Empty { get; } = new();

    /// <summary>Gets a value indicating whether the policy requires local-only operation. A value of false never relaxes anything.</summary>
    public bool LocalOnly { get; init; }

    /// <summary>Gets the tools that are disabled in this repository.</summary>
    public IReadOnlyList<string> DisabledTools { get; init; } = [];

    /// <summary>
    /// Gets the features (names from <see cref="FeatureNames"/>) that are switched off in this repository. A feature
    /// can only be switched off, never on.
    /// </summary>
    public IReadOnlyList<string> DisabledFeatures { get; init; } = [];

    /// <summary>Gets glob patterns that are excluded from indexing and context in this repository.</summary>
    public IReadOnlyList<string> Exclude { get; init; } = [];

    /// <summary>Gets commands the agent must never run in this repository.</summary>
    public IReadOnlyList<string> DenyCommands { get; init; } = [];

    /// <summary>Gets warnings produced while parsing (ignored keys and values of the wrong type).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Reads the policy of a repository.</summary>
    /// <param name="repositoryRoot">The repository root directory.</param>
    /// <param name="logger">An optional logger that receives warnings.</param>
    /// <returns>The parsed policy, or <see cref="Empty"/> when the repository has no policy file.</returns>
    /// <exception cref="SettingsValidationException">The file exists but is not a JSON object.</exception>
    public static RepositoryPolicy LoadFromRepository(string repositoryRoot, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var path = Path.Combine(repositoryRoot, ".pillion", "policy.json");
        if (!File.Exists(path))
        {
            return Empty;
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SettingsValidationException($"The repository policy '{path}' could not be read: {ex.Message}", ex);
        }

        return Parse(json, logger, path);
    }

    /// <summary>Parses the text of a policy file.</summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="logger">An optional logger that receives warnings.</param>
    /// <param name="source">A description of where the text came from, used in messages.</param>
    /// <returns>The parsed policy.</returns>
    /// <exception cref="SettingsValidationException">The text is not a JSON object.</exception>
    public static RepositoryPolicy Parse(string json, ILogger? logger = null, string source = RelativePath)
    {
        ArgumentNullException.ThrowIfNull(json);
        logger ??= NullLogger.Instance;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, SettingsJson.DocumentOptions);
        }
        catch (JsonException ex)
        {
            throw new SettingsValidationException($"The repository policy '{source}' is not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new SettingsValidationException($"The repository policy '{source}' must be a JSON object.");
            }

            var warnings = new List<string>();
            var localOnly = false;
            IReadOnlyList<string> disabledTools = [];
            IReadOnlyList<string> disabledFeatures = [];
            IReadOnlyList<string> exclude = [];
            IReadOnlyList<string> denyCommands = [];

            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "localOnly":
                        if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        {
                            localOnly = property.Value.GetBoolean();
                        }
                        else
                        {
                            warnings.Add($"'{property.Name}' must be true or false; ignored.");
                        }

                        break;
                    case "disabledTools":
                        disabledTools = ReadStrings(property, warnings);
                        break;
                    case "disabledFeatures":
                        disabledFeatures = ReadStrings(property, warnings);
                        break;
                    case "exclude":
                        exclude = ReadStrings(property, warnings);
                        break;
                    case "denyCommands":
                        denyCommands = ReadStrings(property, warnings);
                        break;
                    default:
                        warnings.Add($"'{property.Name}' is not a recognised policy key; ignored (policies can only restrict).");
                        break;
                }
            }

            foreach (var warning in warnings)
            {
                LogWarning(logger, source, warning);
            }

            return new RepositoryPolicy
            {
                LocalOnly = localOnly,
                DisabledTools = disabledTools,
                DisabledFeatures = disabledFeatures,
                Exclude = exclude,
                DenyCommands = denyCommands,
                Warnings = warnings,
            };
        }
    }

    private static List<string> ReadStrings(JsonProperty property, List<string> warnings)
    {
        var result = new List<string>();
        if (property.Value.ValueKind != JsonValueKind.Array)
        {
            warnings.Add($"'{property.Name}' must be an array of strings; ignored.");
            return result;
        }

        foreach (var item in property.Value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            {
                result.Add(item.GetString()!);
            }
            else
            {
                warnings.Add($"'{property.Name}' contains an entry that is not a non-empty string; entry ignored.");
            }
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Repository policy '{Source}': {Warning}")]
    private static partial void LogWarning(ILogger logger, string source, string warning);
}

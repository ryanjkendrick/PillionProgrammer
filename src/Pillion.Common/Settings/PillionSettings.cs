using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Pillion.Common.Hardware;
using Pillion.Contracts;

namespace Pillion.Common.Settings;

/// <summary>The kind of a configured model provider.</summary>
public enum ProviderKind
{
    /// <summary>A local or remote Ollama server.</summary>
    [JsonStringEnumMemberName("ollama")]
    Ollama,

    /// <summary>Any server that implements the OpenAI chat completions API.</summary>
    [JsonStringEnumMemberName("openai-compatible")]
    OpenAiCompatible,

    /// <summary>The Anthropic API.</summary>
    [JsonStringEnumMemberName("anthropic")]
    Anthropic,

    /// <summary>The OpenAI API.</summary>
    [JsonStringEnumMemberName("openai")]
    OpenAi,

    /// <summary>Azure OpenAI Service.</summary>
    [JsonStringEnumMemberName("azure-openai")]
    AzureOpenAi,

    /// <summary>The Google Gemini API.</summary>
    [JsonStringEnumMemberName("gemini")]
    Gemini,

    /// <summary>The Mistral API.</summary>
    [JsonStringEnumMemberName("mistral")]
    Mistral,
}

/// <summary>How much the agent may do without asking.</summary>
public enum ApprovalMode
{
    /// <summary>Ask before anything that changes state.</summary>
    [JsonStringEnumMemberName("ask")]
    Ask,

    /// <summary>Run safe, read-only actions automatically and ask for the rest.</summary>
    [JsonStringEnumMemberName("auto-safe")]
    AutoSafe,

    /// <summary>Run everything without asking, except actions that policy denies.</summary>
    [JsonStringEnumMemberName("autonomous")]
    Autonomous,
}

/// <summary>A configured model provider. Credentials are never stored here; they live in the secret store.</summary>
public sealed class ProviderSettings : SettingsNode
{
    /// <summary>Gets or sets the provider id, which is also the name of its secret (for example <c>openai</c>).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the provider kind.</summary>
    public ProviderKind Kind { get; set; } = ProviderKind.OpenAiCompatible;

    /// <summary>Gets or sets the name shown to the user.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the endpoint URL, or an empty string for the provider's default.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Gets or sets the ids of the models Pillion may use from this provider.</summary>
    public List<string> Models { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether the provider is used.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>Maps task classes and profiles to models.</summary>
public sealed class RoutingSettings : SettingsNode
{
    /// <summary>Gets or sets the profile used for each task class (<see cref="Profile.Auto"/> by default).</summary>
    public Dictionary<TaskClass, Profile> Tasks { get; set; } = CreateDefaultTasks();

    /// <summary>Gets or sets the model id pinned to each profile; an empty string lets the model catalog decide.</summary>
    public Dictionary<Profile, string> Profiles { get; set; } = CreateDefaultProfiles();

    /// <summary>
    /// Gets or sets a value indicating whether "Think harder" is on by default: Auto routes to a more capable model
    /// and asks the provider for more reasoning effort. Off by default. The gateway reads it in a later work item.
    /// </summary>
    public bool ThinkHarder { get; set; }

    private static Dictionary<TaskClass, Profile> CreateDefaultTasks()
    {
        var map = new Dictionary<TaskClass, Profile>();
        foreach (var task in Enum.GetValues<TaskClass>())
        {
            map[task] = Profile.Auto;
        }

        return map;
    }

    private static Dictionary<Profile, string> CreateDefaultProfiles()
    {
        var map = new Dictionary<Profile, string>();
        foreach (var profile in Enum.GetValues<Profile>())
        {
            map[profile] = string.Empty;
        }

        return map;
    }
}

/// <summary>Local model settings (design section 6, Local model residency).</summary>
public sealed class ModelsSettings : SettingsNode
{
    /// <summary>
    /// Gets or sets the GPU tier: <see cref="GpuTierSetting.Auto"/> (<c>auto</c>, the default) detects it from the
    /// adapter's dedicated memory; <c>cpu</c>, <c>8gb</c>, <c>12gb</c>, <c>16gb</c> or <c>24gb</c> override detection.
    /// The tier picks the resident local models and the autocomplete prompt budget.
    /// </summary>
    public GpuTierSetting GpuTier { get; set; } = GpuTierSetting.Auto;

    /// <summary>
    /// Gets or sets the context size (<c>num_ctx</c>) a local model is kept resident at, by model id, for example
    /// <c>{ "qwen2.5-coder:7b": 8192 }</c> (<c>models.residentContext.&lt;model&gt;</c>). Without an entry the catalog's recommended
    /// size for the GPU tier applies to a model shared between autocomplete and chat. Model ids contain dots, so set the
    /// whole object in <c>settings.json</c> rather than a dotted key path.
    /// </summary>
    public Dictionary<string, int> ResidentContext { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Which task classes may use cloud providers. A cloud key enables chat only; autocomplete, agent and self-healing
/// each need their own switch.
/// </summary>
public sealed class CloudSwitchSettings : SettingsNode
{
    /// <summary>Gets or sets a value indicating whether chat may use cloud providers.</summary>
    public bool Chat { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether inline edits may use cloud providers.</summary>
    public bool InlineEdit { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether autocomplete may use cloud providers.</summary>
    public bool Autocomplete { get; set; }

    /// <summary>Gets or sets a value indicating whether the agent may use cloud providers.</summary>
    public bool Agent { get; set; }

    /// <summary>Gets or sets a value indicating whether self-healing may use cloud providers.</summary>
    public bool SelfHealing { get; set; }

    /// <summary>Gets or sets a value indicating whether vision requests may use cloud providers.</summary>
    public bool Vision { get; set; } = true;
}

/// <summary>Privacy settings.</summary>
public sealed class PrivacySettings : SettingsNode
{
    /// <summary>Gets or sets a value indicating whether nothing may leave this machine.</summary>
    public bool LocalOnly { get; set; }

    /// <summary>Gets or sets a value indicating whether the user opted in to telemetry.</summary>
    public bool TelemetryOptIn { get; set; }

    /// <summary>Gets or sets a value indicating whether prompts sent to local providers are redacted too.</summary>
    public bool RedactForLocalProviders { get; set; }
}

/// <summary>Approval settings.</summary>
public sealed class ApprovalSettings : SettingsNode
{
    /// <summary>Gets or sets the approval mode.</summary>
    public ApprovalMode Mode { get; set; } = ApprovalMode.Ask;
}

/// <summary>Spending limits for one provider. A <see langword="null"/> limit means no limit.</summary>
public sealed class CostCap : SettingsNode
{
    /// <summary>Gets or sets the daily limit in US dollars.</summary>
    public decimal? DailyUsd { get; set; }

    /// <summary>Gets or sets the monthly limit in US dollars.</summary>
    public decimal? MonthlyUsd { get; set; }
}

/// <summary>Code indexing settings.</summary>
public sealed class IndexingSettings : SettingsNode
{
    /// <summary>Gets or sets glob patterns of paths that are never indexed.</summary>
    public List<string> Exclude { get; set; } = [];

    /// <summary>Gets or sets the most memory, in megabytes, that indexing may use.</summary>
    public int MaxMemoryMb { get; set; } = 1536;

    /// <summary>Gets or sets a value indicating whether indexing pauses while on battery power.</summary>
    public bool PauseOnBattery { get; set; } = true;

    /// <summary>
    /// Gets or sets how many days an index may go unopened before it is deleted (design section 10, Retention). Zero or
    /// less turns the age rule off; an index whose repository folder no longer exists is deleted either way.
    /// </summary>
    public int RetentionDays { get; set; } = 30;
}

/// <summary>Logging settings.</summary>
public sealed class LoggingSettings : SettingsNode
{
    /// <summary>Gets or sets the minimum level that is logged.</summary>
    public LogLevel Level { get; set; } = LogLevel.Information;

    /// <summary>Gets or sets a value indicating whether RPC traffic is traced (it can contain code).</summary>
    public bool RpcTrace { get; set; }
}

/// <summary>The complete contents of <c>settings.json</c>.</summary>
public sealed class PillionSettings : SettingsNode
{
    /// <summary>The current schema version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Gets or sets the schema version of the file.</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>Gets or sets the configured model providers.</summary>
    public List<ProviderSettings> Providers { get; set; } = [];

    /// <summary>Gets or sets the routing settings.</summary>
    public RoutingSettings Routing { get; set; } = new();

    /// <summary>Gets or sets the local model settings.</summary>
    public ModelsSettings Models { get; set; } = new();

    /// <summary>Gets or sets the cloud switches.</summary>
    public CloudSwitchSettings CloudSwitches { get; set; } = new();

    /// <summary>Gets or sets the privacy settings.</summary>
    public PrivacySettings Privacy { get; set; } = new();

    /// <summary>Gets or sets the approval settings.</summary>
    public ApprovalSettings Approvals { get; set; } = new();

    /// <summary>Gets or sets the cost caps, keyed by provider id.</summary>
    public Dictionary<string, CostCap> CostCaps { get; set; } = [];

    /// <summary>Gets or sets the indexing settings.</summary>
    public IndexingSettings Indexing { get; set; } = new();

    /// <summary>Gets or sets the logging settings.</summary>
    public LoggingSettings Logging { get; set; } = new();

    /// <summary>Gets or sets the feature flags.</summary>
    public FeatureSettings Features { get; set; } = new();
}

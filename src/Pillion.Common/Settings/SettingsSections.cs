namespace Pillion.Common.Settings;

/// <summary>The names of the top-level keys in <c>settings.json</c>.</summary>
public static class SettingsSections
{
    /// <summary>The schema version key.</summary>
    public const string Version = "version";

    /// <summary>The model providers section.</summary>
    public const string Providers = "providers";

    /// <summary>The routing section.</summary>
    public const string Routing = "routing";

    /// <summary>The local models section (GPU tier override).</summary>
    public const string Models = "models";

    /// <summary>The per-task-class cloud switches section.</summary>
    public const string CloudSwitches = "cloudSwitches";

    /// <summary>The privacy section.</summary>
    public const string Privacy = "privacy";

    /// <summary>The approvals section.</summary>
    public const string Approvals = "approvals";

    /// <summary>The per-provider cost caps section.</summary>
    public const string CostCaps = "costCaps";

    /// <summary>The indexing section.</summary>
    public const string Indexing = "indexing";

    /// <summary>The chat mentions section (<c>mentions.docsFolders</c>, work item 2.13).</summary>
    public const string Mentions = "mentions";

    /// <summary>The logging section.</summary>
    public const string Logging = "logging";

    /// <summary>The feature flags section.</summary>
    public const string Features = "features";

    /// <summary>The sections that hold typed data, in the order they are written to a new file.</summary>
    internal static readonly string[] Known =
    [
        Providers,
        Routing,
        Models,
        CloudSwitches,
        Privacy,
        Approvals,
        CostCaps,
        Indexing,
        Mentions,
        Logging,
        Features,
    ];
}

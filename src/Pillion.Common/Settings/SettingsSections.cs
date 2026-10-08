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

    /// <summary>The logging section.</summary>
    public const string Logging = "logging";

    /// <summary>The feature flags section.</summary>
    public const string Features = "features";

    /// <summary>The sections that hold typed data, in the order they are written to a new file.</summary>
    internal static readonly string[] Known =
    [
        Providers,
        Routing,
        CloudSwitches,
        Privacy,
        Approvals,
        CostCaps,
        Indexing,
        Logging,
        Features,
    ];
}

using System.Text.Json;

namespace Pillion.Common.Settings;

/// <summary>
/// The settings Pillion actually runs with after repository policies are applied.
/// Precedence is defaults &lt; user settings &lt; policy restrictions, and a policy can only restrict.
/// </summary>
public sealed class EffectiveSettings
{
    private EffectiveSettings(
        PillionSettings settings,
        IReadOnlyList<string> disabledTools,
        IReadOnlyList<string> disabledFeatures,
        IReadOnlyList<string> denyCommands)
    {
        Settings = settings;
        DisabledTools = disabledTools;
        DisabledFeatures = disabledFeatures;
        DenyCommands = denyCommands;
    }

    /// <summary>
    /// Gets a copy of the user settings with policy restrictions applied
    /// (<c>privacy.localOnly</c> turned on and <c>indexing.exclude</c> extended where a policy asks).
    /// </summary>
    public PillionSettings Settings { get; }

    /// <summary>Gets a value indicating whether nothing may leave this machine.</summary>
    public bool LocalOnly => Settings.Privacy.LocalOnly;

    /// <summary>Gets the union of the tools disabled by all policies.</summary>
    public IReadOnlyList<string> DisabledTools { get; }

    /// <summary>
    /// Gets the union of the features disabled by all policies, as written in the policies (including names this
    /// version does not know). The known ones are also switched off in <see cref="PillionSettings.Features"/>.
    /// </summary>
    public IReadOnlyList<string> DisabledFeatures { get; }

    /// <summary>Gets the union of the glob patterns excluded by the user and all policies.</summary>
    public IReadOnlyList<string> Exclude => Settings.Indexing.Exclude;

    /// <summary>Gets the union of the commands denied by all policies.</summary>
    public IReadOnlyList<string> DenyCommands { get; }

    /// <summary>Determines whether a feature is on: the user has not switched it off and no policy disables it.</summary>
    /// <param name="feature">A name from <see cref="FeatureNames"/>; case is ignored.</param>
    /// <returns><see langword="true"/> when the feature is on. A feature this version does not know is on unless a policy lists it.</returns>
    public bool IsFeatureEnabled(string feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        return Settings.Features.IsEnabled(feature)
            && !DisabledFeatures.Contains(feature.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Serialises the effective feature flags as a JSON object, for example <c>{"chat":true,"modelGateway":false,...}</c>.</summary>
    /// <returns>The JSON text, with camelCase names as in <c>settings.json</c>.</returns>
    public string FeaturesToJson() => JsonSerializer.Serialize(Settings.Features, SettingsJson.Options);

    /// <summary>Combines user settings with repository policies.</summary>
    /// <param name="userSettings">The user's settings (which already include defaults). They are not modified.</param>
    /// <param name="policies">The policies of the repositories in scope.</param>
    /// <returns>The effective settings.</returns>
    public static EffectiveSettings Resolve(PillionSettings userSettings, IEnumerable<RepositoryPolicy>? policies)
    {
        ArgumentNullException.ThrowIfNull(userSettings);

        // Work on a deep copy so the caller's object is never changed.
        var copy = JsonSerializer.Deserialize<PillionSettings>(
            JsonSerializer.SerializeToUtf8Bytes(userSettings, SettingsJson.Options),
            SettingsJson.Options)!;

        var exclude = new List<string>(copy.Indexing.Exclude);
        var disabledTools = new List<string>();
        var disabledFeatures = new List<string>();
        var denyCommands = new List<string>();

        foreach (var policy in policies ?? [])
        {
            if (policy.LocalOnly)
            {
                copy.Privacy.LocalOnly = true;
            }

            AddDistinct(exclude, policy.Exclude);
            AddDistinct(disabledTools, policy.DisabledTools);
            AddDistinct(disabledFeatures, policy.DisabledFeatures);
            AddDistinct(denyCommands, policy.DenyCommands);
        }

        copy.Indexing.Exclude = exclude.Distinct(StringComparer.Ordinal).ToList();
        foreach (var feature in disabledFeatures)
        {
            copy.Features.Disable(feature);
        }

        return new EffectiveSettings(copy, disabledTools, disabledFeatures, denyCommands);
    }

    private static void AddDistinct(List<string> target, IEnumerable<string> source)
    {
        foreach (var item in source)
        {
            if (!target.Contains(item, StringComparer.Ordinal))
            {
                target.Add(item);
            }
        }
    }
}

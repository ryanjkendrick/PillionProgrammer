using System.Text.Json.Nodes;

namespace Pillion.Common.Settings;

/// <summary>Upgrades the settings document by one schema version.</summary>
public interface ISettingsMigration
{
    /// <summary>Gets the version this migration upgrades from; the result is <c>FromVersion + 1</c>.</summary>
    int FromVersion { get; }

    /// <summary>Transforms the document in place. The store sets the <c>version</c> key afterwards.</summary>
    /// <param name="root">The settings document at version <see cref="FromVersion"/>.</param>
    void Migrate(JsonObject root);
}

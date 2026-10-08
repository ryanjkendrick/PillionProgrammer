using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pillion.Common.Settings;

/// <summary>
/// Base class for typed settings objects. Properties that this version of Pillion does not know about are kept in
/// <see cref="ExtensionData"/> so that they survive a read-modify-write cycle (a newer or hand-edited file is not damaged).
/// </summary>
public abstract class SettingsNode
{
    /// <summary>Gets or sets properties present in the JSON that are not modelled by this type.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

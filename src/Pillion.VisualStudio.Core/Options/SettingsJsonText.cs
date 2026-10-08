using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// Reads and writes the JSON values exchanged through <c>ISettingsService.GetAsync</c> and <c>SetAsync</c>. The host's
/// <c>settings.json</c> uses camelCase names and string enums (for example <c>"kind": "openai-compatible"</c>).
/// </summary>
public static class SettingsJsonText
{
    /// <summary>The key of the providers list.</summary>
    public const string ProvidersKey = "providers";

    /// <summary>The key of the Local-only switch.</summary>
    public const string LocalOnlyKey = "privacy.localOnly";

    /// <summary>The key of the cloud switches object.</summary>
    public const string CloudSwitchesKey = "cloudSwitches";

    private static readonly JsonSerializerSettings ReadSettings = new()
    {
        DateParseHandling = DateParseHandling.None,
        MaxDepth = 64,
    };

    /// <summary>Parses a JSON value.</summary>
    /// <param name="json">The JSON text, or <see langword="null"/> when the setting is not set.</param>
    /// <returns>The token, or <see langword="null"/>.</returns>
    /// <exception cref="JsonException">The text is not valid JSON.</exception>
    public static JToken? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonConvert.DeserializeObject<JToken>(json!, ReadSettings);
    }

    /// <summary>Reads a boolean setting.</summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="fallback">The value when the setting is not set or not a boolean.</param>
    /// <returns>The value.</returns>
    public static bool ReadBool(string? json, bool fallback) =>
        Parse(json) is JValue { Type: JTokenType.Boolean } value ? (bool)value : fallback;

    /// <summary>Reads an object setting's properties (for example <c>cloudSwitches</c>).</summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The object, or an empty one when the setting is not set or not an object.</returns>
    public static JObject ReadObject(string? json) => Parse(json) as JObject ?? [];

    /// <summary>Reads the providers list: each entry is kept whole so unknown properties survive a save.</summary>
    /// <param name="json">The JSON text of the <c>providers</c> setting.</param>
    /// <returns>The provider objects.</returns>
    public static IReadOnlyList<JObject> ReadProviders(string? json)
    {
        var list = new List<JObject>();
        if (Parse(json) is JArray array)
        {
            foreach (var item in array)
            {
                if (item is JObject provider)
                {
                    list.Add(provider);
                }
            }
        }

        return list;
    }

    /// <summary>Reads the providers list as the chat window needs it.</summary>
    /// <param name="json">The JSON text of the <c>providers</c> setting.</param>
    /// <returns>The providers.</returns>
    public static IReadOnlyList<ProviderSummary> ReadProviderSummaries(string? json)
    {
        var list = new List<ProviderSummary>();
        foreach (var provider in ReadProviders(json))
        {
            list.Add(new ProviderSummary(
                GetString(provider, "id"),
                GetString(provider, "displayName"),
                provider["enabled"] is JValue { Type: JTokenType.Boolean } enabled ? (bool)enabled : true));
        }

        return list;
    }

    /// <summary>Writes a boolean as JSON.</summary>
    /// <param name="value">The value.</param>
    /// <returns><c>true</c> or <c>false</c>.</returns>
    public static string Write(bool value) => value ? "true" : "false";

    /// <summary>Writes a token as compact JSON.</summary>
    /// <param name="token">The token.</param>
    /// <returns>The JSON text.</returns>
    public static string Write(JToken token)
    {
        if (token is null)
        {
            throw new ArgumentNullException(nameof(token));
        }

        return token.ToString(Formatting.None);
    }

    /// <summary>Gets a string property, or an empty string.</summary>
    /// <param name="obj">The object.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The value.</returns>
    public static string GetString(JObject obj, string name) =>
        obj?[name] is JValue { Type: JTokenType.String } value ? (string?)value ?? string.Empty : string.Empty;
}

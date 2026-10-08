using System.Globalization;
using System.Text.Json.Nodes;

namespace Pillion.Common.Settings;

/// <summary>
/// Reads and writes values in a JSON document by dotted key path, for example <c>routing.tasks.chat</c> or
/// <c>providers.0.endpoint</c>. Numeric segments index arrays. Property names that contain a dot cannot be addressed.
/// </summary>
internal static class SettingsKeyPath
{
    /// <summary>Splits and validates a key path.</summary>
    /// <param name="keyPath">The dotted key path.</param>
    /// <returns>The segments.</returns>
    /// <exception cref="SettingsValidationException">The path is empty or has an empty segment.</exception>
    public static string[] Parse(string keyPath)
    {
        if (string.IsNullOrWhiteSpace(keyPath))
        {
            throw new SettingsValidationException("A settings key must not be empty.");
        }

        var segments = keyPath.Split('.');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment != segment.Trim())
            {
                throw new SettingsValidationException($"The settings key '{keyPath}' has an empty or padded segment.");
            }
        }

        return segments;
    }

    /// <summary>Finds the node at a key path.</summary>
    /// <param name="root">The document.</param>
    /// <param name="segments">The parsed key path.</param>
    /// <returns>The node, or <see langword="null"/> if the path does not exist or holds JSON null.</returns>
    public static JsonNode? Find(JsonNode root, string[] segments)
    {
        JsonNode? current = root;
        foreach (var segment in segments)
        {
            switch (current)
            {
                case JsonObject obj:
                    if (!obj.TryGetPropertyValue(segment, out current))
                    {
                        return null;
                    }

                    break;
                case JsonArray array:
                    if (!TryParseIndex(segment, out var index) || index >= array.Count)
                    {
                        return null;
                    }

                    current = array[index];
                    break;
                default:
                    return null;
            }
        }

        return current;
    }

    /// <summary>Sets the node at a key path, creating intermediate objects as needed.</summary>
    /// <param name="root">The document, which is modified.</param>
    /// <param name="segments">The parsed key path.</param>
    /// <param name="value">The new value (a node that has no parent yet).</param>
    /// <exception cref="SettingsValidationException">The path cannot be created, or an array index is out of range.</exception>
    public static void Set(JsonObject root, string[] segments, JsonNode? value)
    {
        JsonNode current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            JsonNode? next;
            switch (current)
            {
                case JsonObject obj:
                    if (!obj.TryGetPropertyValue(segment, out next) || next is null)
                    {
                        next = new JsonObject();
                        obj[segment] = next;
                    }

                    break;
                case JsonArray array:
                    if (!TryParseIndex(segment, out var index) || index >= array.Count || array[index] is null)
                    {
                        throw new SettingsValidationException($"'{string.Join('.', segments.Take(i + 1))}' does not exist, so '{string.Join('.', segments)}' cannot be set.");
                    }

                    next = array[index]!;
                    break;
                default:
                    throw new SettingsValidationException($"'{string.Join('.', segments.Take(i))}' is not an object or array, so '{string.Join('.', segments)}' cannot be set.");
            }

            if (next is not (JsonObject or JsonArray))
            {
                throw new SettingsValidationException($"'{string.Join('.', segments.Take(i + 1))}' is not an object or array, so '{string.Join('.', segments)}' cannot be set.");
            }

            current = next;
        }

        var last = segments[^1];
        switch (current)
        {
            case JsonObject target:
                target[last] = value;
                break;
            case JsonArray array:
                if (!TryParseIndex(last, out var position) || position > array.Count)
                {
                    throw new SettingsValidationException($"'{last}' is not a valid index into '{string.Join('.', segments.Take(segments.Length - 1))}' (it has {array.Count} items; use {array.Count} to append).");
                }

                if (position == array.Count)
                {
                    array.Add(value);
                }
                else
                {
                    array[position] = value;
                }

                break;
        }
    }

    private static bool TryParseIndex(string segment, out int index)
        => int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out index);
}

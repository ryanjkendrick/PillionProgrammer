using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Pillion.Common.Settings;

/// <summary>The JSON conventions of <c>settings.json</c>: camelCase names, string enums, indented and stable.</summary>
internal static class SettingsJson
{
    /// <summary>Options for reading and writing typed settings.</summary>
    public static readonly JsonSerializerOptions Options = CreateOptions(indented: false);

    /// <summary>Options for writing the document to disk.</summary>
    public static readonly JsonSerializerOptions IndentedOptions = CreateOptions(indented: true);

    /// <summary>Options for parsing the file: comments and trailing commas are tolerated.</summary>
    public static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Serialises the default settings to a document.</summary>
    /// <returns>A new object holding every section at its default.</returns>
    public static JsonObject CreateDefaultDocument()
        => (JsonObject)JsonSerializer.SerializeToNode(new PillionSettings(), Options)!;

    private static JsonSerializerOptions CreateOptions(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = indented,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

        // Fill in defaults for properties that are missing from the file instead of replacing the default collections.
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

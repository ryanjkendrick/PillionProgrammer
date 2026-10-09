using System.Text.Json;
using System.Text.RegularExpressions;
using Pillion.Contracts;

namespace Pillion.ModelGateway.Editing;

/// <summary>
/// The inline chat prompts, shipped as data in <c>Templates/inline-edit.json</c> (design section 6, Prompt templates):
/// the system prompts for edits, tests and explanations, the task wording per slash command, the prompt sections and the
/// corrective messages sent when a reply cannot be used. Placeholders are written <c>{name}</c>.
/// </summary>
public sealed partial class InlineEditTemplates
{
    private readonly Dictionary<string, string> _system;
    private readonly Dictionary<string, string> _commands;
    private readonly Dictionary<string, string> _docStyles;
    private readonly Dictionary<string, string> _languages;
    private readonly Dictionary<string, string> _sections;
    private readonly Dictionary<string, string> _corrections;

    private InlineEditTemplates(
        Dictionary<string, string> system,
        Dictionary<string, string> commands,
        Dictionary<string, string> docStyles,
        Dictionary<string, string> languages,
        Dictionary<string, string> sections,
        Dictionary<string, string> corrections)
    {
        _system = system;
        _commands = commands;
        _docStyles = docStyles;
        _languages = languages;
        _sections = sections;
        _corrections = corrections;
    }

    /// <summary>Gets the templates shipped in this assembly.</summary>
    public static InlineEditTemplates Default { get; } = Parse(ReadResource("Pillion.ModelGateway.Templates.inline-edit.json"));

    /// <summary>Parses the template document.</summary>
    /// <param name="json">The contents of <c>inline-edit.json</c>.</param>
    /// <returns>The templates.</returns>
    /// <exception cref="FormatException">The document is not valid or lacks a required entry.</exception>
    public static InlineEditTemplates Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var result = new InlineEditTemplates(
                Read(root, "system", "edit", "test", "explain"),
                Read(root, "commands", "none", "doc", "test", "fix", "explain"),
                Read(root, "docStyles", "default"),
                Read(root, "languages"),
                Read(root, "sections", "header", "before", "scope", "proposal", "after", "diagnostics", "history", "task", "instruction"),
                Read(root, "corrections", "unmatched", "malformed", "noBlocks", "noCode", "syntax", "placeholders"));
            return result;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new FormatException($"inline-edit.json is not valid: {ex.Message}", ex);
        }
    }

    /// <summary>Gets the system prompt for a command.</summary>
    /// <param name="command">The slash command.</param>
    /// <param name="language">The language's display name.</param>
    /// <returns>The system prompt.</returns>
    public string SystemPrompt(InlineEditCommand command, string language) => Render(
        _system[command switch
        {
            InlineEditCommand.Test => "test",
            InlineEditCommand.Explain => "explain",
            _ => "edit",
        }],
        ("language", language));

    /// <summary>Gets the task wording for a command.</summary>
    /// <param name="command">The slash command.</param>
    /// <param name="languageId">The language id (chooses the documentation comment style for <c>/doc</c>).</param>
    /// <returns>The task.</returns>
    public string Task(InlineEditCommand command, string languageId)
    {
        var key = command.ToString().ToLowerInvariant();
        var docStyle = _docStyles.TryGetValue(languageId ?? string.Empty, out var style) ? style : _docStyles["default"];
        return Render(_commands[key], ("docStyle", docStyle));
    }

    /// <summary>Gets the display name of a language.</summary>
    /// <param name="languageId">The language id.</param>
    /// <returns>The name, or the id itself when it is not known ("code" when empty).</returns>
    public string LanguageName(string? languageId) =>
        string.IsNullOrWhiteSpace(languageId) ? "code"
        : _languages.TryGetValue(languageId, out var name) ? name
        : languageId;

    /// <summary>Renders a prompt section.</summary>
    /// <param name="name">The section name (<c>header</c>, <c>before</c>, <c>scope</c>, <c>proposal</c>, <c>after</c>, <c>diagnostics</c>, <c>history</c>, <c>task</c>, <c>instruction</c>).</param>
    /// <param name="values">The placeholder values.</param>
    /// <returns>The section text.</returns>
    public string Section(string name, params (string Name, string Value)[] values) => Render(_sections[name], values);

    /// <summary>Renders a corrective message.</summary>
    /// <param name="name">The message name (<c>unmatched</c>, <c>malformed</c>, <c>noBlocks</c>, <c>noCode</c>, <c>syntax</c>, <c>placeholders</c>).</param>
    /// <param name="values">The placeholder values.</param>
    /// <returns>The message text.</returns>
    public string Correction(string name, params (string Name, string Value)[] values) => Render(_corrections[name], values);

    /// <summary>Replaces <c>{name}</c> placeholders in one pass, so a value that contains braces is never expanded.</summary>
    /// <param name="template">The template.</param>
    /// <param name="values">The values.</param>
    /// <returns>The rendered text; unknown placeholders are left as they are.</returns>
    public static string Render(string template, params (string Name, string Value)[] values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        return Placeholder().Replace(template, m =>
        {
            foreach (var (name, value) in values)
            {
                if (string.Equals(name, m.Groups[1].Value, StringComparison.Ordinal))
                {
                    return value;
                }
            }

            return m.Value;
        });
    }

    private static Dictionary<string, string> Read(JsonElement root, string section, params string[] required)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.GetProperty(section).EnumerateObject())
        {
            result[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        foreach (var key in required)
        {
            if (!result.ContainsKey(key))
            {
                throw new KeyNotFoundException($"'{section}' must define '{key}'.");
            }
        }

        return result;
    }

    private static string ReadResource(string name)
    {
        using var stream = typeof(InlineEditTemplates).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The embedded resource {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"\{([A-Za-z]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}

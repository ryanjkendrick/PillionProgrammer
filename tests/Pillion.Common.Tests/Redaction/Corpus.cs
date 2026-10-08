using System.Text.RegularExpressions;
using Pillion.Common.Redaction;

namespace Pillion.Common.Tests.Redaction;

/// <summary>A positive corpus sample with its expectations.</summary>
/// <param name="Name">The file name.</param>
/// <param name="Text">The text with every <c>{{kind:variant}}</c> macro expanded into a fabricated secret.</param>
/// <param name="ExpectedKinds">The kind ids the scanner must report, in document order.</param>
/// <param name="ExpectedValues">The exact secret values the scanner must report, in document order.</param>
internal sealed record CorpusSample(string Name, string Text, IReadOnlyList<string> ExpectedKinds, IReadOnlyList<string> ExpectedValues);

/// <summary>
/// Loads the files under <c>Redaction/Corpus</c>. Positive files are named <c>kinds.label.ext</c> where kinds is a
/// <c>+</c> separated list of kind ids in document order, each optionally followed by <c>~n</c> for n repeats of that
/// kind. Their text contains <c>{{kind}}</c> or <c>{{kind:variant}}</c> macros that are expanded into fabricated
/// secrets at load time, so no complete secret-shaped literal is committed. Negative files are used verbatim and
/// must produce no matches.
/// </summary>
internal static partial class Corpus
{
    private static string Root => Path.Combine(AppContext.BaseDirectory, "Redaction", "Corpus");

    public static IEnumerable<object[]> PositiveNames() => Names("positive");

    public static IEnumerable<object[]> NegativeNames() => Names("negative");

    public static CorpusSample LoadPositive(string name)
    {
        var template = File.ReadAllText(Path.Combine(Root, "positive", name));
        var values = new List<string>();
        var text = Macro().Replace(template, m =>
        {
            var id = m.Groups[1].Value;
            var variant = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
            var value = id == "gcp-key-id"
                ? SecretFactory.GcpKeyId(variant)
                : SecretFactory.Create(KindOf(id), variant);
            values.Add(value);
            return value;
        });

        var kinds = new List<string>();
        foreach (var part in name[..name.IndexOf('.', StringComparison.Ordinal)].Split('+'))
        {
            var pieces = part.Split('~');
            var repeats = pieces.Length > 1 ? int.Parse(pieces[1], System.Globalization.CultureInfo.InvariantCulture) : 1;
            for (var i = 0; i < repeats; i++)
            {
                kinds.Add(pieces[0]);
            }
        }

        return new CorpusSample(name, text, kinds, values);
    }

    public static string LoadNegative(string name) => File.ReadAllText(Path.Combine(Root, "negative", name));

    private static IEnumerable<object[]> Names(string folder) =>
        Directory.GetFiles(Path.Combine(Root, folder)).Select(f => new object[] { Path.GetFileName(f) }).OrderBy(o => (string)o[0], StringComparer.Ordinal);

    private static SecretKind KindOf(string id) =>
        SecretKindExtensions.TryParseId(id, out var kind) ? kind : throw new InvalidOperationException($"Unknown kind id in corpus macro: {id}");

    [GeneratedRegex(@"\{\{([a-z\-]+)(?::(\d+))?\}\}")]
    private static partial Regex Macro();
}

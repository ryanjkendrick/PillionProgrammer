namespace Pillion.Common.Redaction;

/// <summary>
/// What a <see cref="RedactionSession"/> has redacted so far, for the Context Inspector notice. It never contains
/// the secrets themselves.
/// </summary>
public sealed class RedactionSummary
{
    internal RedactionSummary(int count, int distinctCount, IReadOnlyList<string> kinds, IReadOnlyDictionary<string, int> countsByKind)
    {
        Count = count;
        DistinctCount = distinctCount;
        Kinds = kinds;
        CountsByKind = countsByKind;
    }

    /// <summary>Gets the total number of replacements made (a secret that appears three times counts three).</summary>
    public int Count { get; }

    /// <summary>Gets the number of distinct secret values redacted.</summary>
    public int DistinctCount { get; }

    /// <summary>Gets the distinct kind ids (for example <c>aws-access-key</c>) in order of first appearance.</summary>
    public IReadOnlyList<string> Kinds { get; }

    /// <summary>Gets the number of replacements per kind id.</summary>
    public IReadOnlyDictionary<string, int> CountsByKind { get; }

    /// <summary>
    /// Gets the notice text: <c>3 redactions: aws-access-key, jwt</c>, <c>1 redaction: jwt</c> or
    /// <c>No redactions</c>.
    /// </summary>
    /// <returns>The notice text.</returns>
    public string Describe() => Count == 0
        ? "No redactions"
        : $"{Count} {(Count == 1 ? "redaction" : "redactions")}: {string.Join(", ", Kinds)}";

    /// <inheritdoc />
    public override string ToString() => Describe();
}

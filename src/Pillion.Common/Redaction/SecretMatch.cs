namespace Pillion.Common.Redaction;

/// <summary>A secret found by <see cref="SecretScanner"/>.</summary>
/// <param name="Kind">Which pattern matched.</param>
/// <param name="Start">Zero-based index of the first character of the secret value.</param>
/// <param name="Length">Length of the secret value in characters.</param>
/// <param name="Severity">How damaging disclosure would be.</param>
/// <remarks>
/// The span covers the secret value only, not the surrounding context. For <c>Password=hunter2;</c> it covers
/// <c>hunter2</c>; for <c>"apiKey": "..."</c> it covers the characters between the quotes. Matches returned from one
/// scan never overlap.
/// </remarks>
public readonly record struct SecretMatch(SecretKind Kind, int Start, int Length, SecretSeverity Severity)
{
    /// <summary>Gets the index one past the last character of the secret.</summary>
    public int End => Start + Length;

    /// <summary>Gets the stable string id of <see cref="Kind"/>.</summary>
    public string KindId => Kind.ToId();
}

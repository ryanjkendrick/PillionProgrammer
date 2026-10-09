using System;
using System.Globalization;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>
/// Proposal ids that mark a ghost-text proposal as Pillion's. Visual Studio's suggestion service reports every inline
/// proposal under the provider name <c>IntelliCodeLineCompletions</c>, whoever produced it, so the shown, accepted and
/// rejected events are matched to Pillion's attempts by this id alone.
/// </summary>
/// <remarks>Format: <c>pillion:&lt;attempt&gt;:&lt;variant&gt;</c>, for example <c>pillion:42:0</c>.</remarks>
public static class PillionProposalId
{
    /// <summary>The prefix every Pillion proposal id starts with.</summary>
    public const string Prefix = "pillion:";

    /// <summary>Creates the id of a proposal.</summary>
    /// <param name="attempt">The attempt number (unique per Visual Studio process).</param>
    /// <param name="variant">The variant shown (0 for the first suggestion, then next and previous).</param>
    /// <returns>The id.</returns>
    public static string Create(long attempt, int variant) =>
        Prefix + attempt.ToString(CultureInfo.InvariantCulture) + ":" + variant.ToString(CultureInfo.InvariantCulture);

    /// <summary>Gets a value indicating whether a proposal id is Pillion's.</summary>
    /// <param name="proposalId">The id, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the id starts with <see cref="Prefix"/>.</returns>
    public static bool IsPillion(string? proposalId) =>
        proposalId is not null && proposalId.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Reads the attempt and variant from a Pillion proposal id.</summary>
    /// <param name="proposalId">The id.</param>
    /// <param name="attempt">The attempt number.</param>
    /// <param name="variant">The variant.</param>
    /// <returns><see langword="true"/> when the id is a well-formed Pillion id.</returns>
    public static bool TryParse(string? proposalId, out long attempt, out int variant)
    {
        attempt = 0;
        variant = 0;
        if (!IsPillion(proposalId))
        {
            return false;
        }

        var rest = proposalId!.Substring(Prefix.Length);
        var colon = rest.IndexOf(':');
        return colon > 0
            && long.TryParse(rest.Substring(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out attempt)
            && int.TryParse(rest.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out variant);
    }
}

using System;
using System.Globalization;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core.Mentions;

/// <summary>
/// A resolved mention pinned to a conversation: a chip above the input with its name and token badge (design section 7,
/// "pins persist for the conversation, each showing its token count"). Its resolution is sent as one of
/// <see cref="ChatRequest.Pins"/> with every message of the conversation.
/// </summary>
public sealed class MentionPin
{
    /// <summary>Initializes a new instance of the <see cref="MentionPin"/> class.</summary>
    /// <param name="resolution">The host's resolution (resolved).</param>
    public MentionPin(MentionResolution resolution)
        : this(resolution, isStale: false)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="MentionPin"/> class.</summary>
    /// <param name="resolution">The resolution, or what a saved pin remembers of one.</param>
    /// <param name="isStale">Whether the pin was restored from the host's store and has no content to send (see <see cref="IsStale"/>).</param>
    public MentionPin(MentionResolution resolution, bool isStale)
    {
        Resolution = resolution ?? throw new ArgumentNullException(nameof(resolution));
        IsStale = isStale;
    }

    /// <summary>
    /// Gets a value indicating whether this pin was restored from a saved conversation and could not be read again (the file
    /// moved, or it holds client-only text such as a selection or the clipboard): the chip shows its saved name and token
    /// count, nothing is sent for it, and pinning it again refreshes it.
    /// </summary>
    public bool IsStale { get; }

    /// <summary>Gets the host's resolution, sent as it is.</summary>
    public MentionResolution Resolution { get; }

    /// <summary>Gets the kind it resolved as.</summary>
    public string Kind => Resolution.Kind;

    /// <summary>Gets the name on the chip.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Resolution.DisplayName) ? Resolution.Mention : Resolution.DisplayName;

    /// <summary>Gets the token count of the content.</summary>
    public int TokenCount => Resolution.TokenCount;

    /// <summary>Gets the badge text, for example <c>1.2k</c>.</summary>
    public string TokenBadge => FormatTokens(TokenCount);

    /// <summary>Gets a value indicating whether the content came from an approved excluded or secret file (the chip shows a lock; it never goes to a cloud model).</summary>
    public bool IsSensitive => Resolution.IsSensitive;

    /// <summary>Gets the chip's tooltip: the path or kind, the token count, and whether it was cut or is sensitive.</summary>
    public string ToolTip
    {
        get
        {
            var text = (string.IsNullOrEmpty(Resolution.FilePath) ? "@" + Kind : Resolution.FilePath) + Environment.NewLine
                + ChatStrings.Format(ChatStrings.PinTokensFormat, TokenCount);
            if (Resolution.Truncated)
            {
                text += Environment.NewLine + ChatStrings.PinTruncated;
            }

            if (IsSensitive)
            {
                text += Environment.NewLine + ChatStrings.PinSensitive;
            }

            if (IsStale)
            {
                text += Environment.NewLine + ChatStrings.PinStale;
            }

            return text;
        }
    }

    /// <summary>Gets the name a screen reader announces for the chip.</summary>
    public string AutomationName =>
        ChatStrings.Format(ChatStrings.PinAutomationNameFormat, DisplayName, TokenCount) + (IsSensitive ? ", " + ChatStrings.PinSensitiveShort : string.Empty) + (IsStale ? ", " + ChatStrings.PinStaleShort : string.Empty);

    /// <summary>Gets the remove button's screen-reader name.</summary>
    public string RemoveAutomationName => ChatStrings.Format(ChatStrings.RemovePinNameFormat, DisplayName);

    /// <summary>Makes what the host stores for the pin: its kind, what it points at, its short name and its size. Never the content.</summary>
    /// <returns>The saved pin.</returns>
    internal ConversationPin ToSaved()
    {
        var kind = Resolution.Kind ?? string.Empty;
        var target = kind is MentionKinds.File or MentionKinds.Folder && !string.IsNullOrEmpty(Resolution.FilePath)
            ? Resolution.FilePath!
            : MentionSyntax.Parse(Resolution.Mention).Value;
        return new ConversationPin { Kind = kind, Target = target, DisplayName = DisplayName, TokenCount = TokenCount };
    }

    /// <summary>Makes a stale pin from what the host stored: its name and token count show, and nothing is sent for it.</summary>
    /// <param name="saved">The saved pin.</param>
    /// <returns>The pin.</returns>
    internal static MentionPin FromSaved(ConversationPin saved)
    {
        var kind = saved.Kind ?? string.Empty;
        var target = saved.Target ?? string.Empty;
        var pathKind = kind is MentionKinds.File or MentionKinds.Folder;
        return new MentionPin(
            new MentionResolution
            {
                Mention = "@" + kind + (target.Length > 0 ? ":" + target : string.Empty),
                Kind = kind,
                DisplayName = saved.DisplayName ?? string.Empty,
                FilePath = pathKind && target.Length > 0 ? target : null,
                TokenCount = Math.Max(0, saved.TokenCount),
                IsResolved = false,
            },
            isStale: true);
    }

    /// <summary>Formats a token count for a badge: <c>950</c>, <c>1.2k</c>, <c>15k</c>.</summary>
    /// <param name="tokens">The count.</param>
    /// <returns>The text.</returns>
    public static string FormatTokens(int tokens) => tokens switch
    {
        < 1000 => tokens.ToString(CultureInfo.CurrentCulture),
        < 10000 => (tokens / 1000.0).ToString("0.0", CultureInfo.CurrentCulture) + "k",
        _ => (tokens / 1000).ToString(CultureInfo.CurrentCulture) + "k",
    };

    /// <summary>Determines whether another resolution pins the same thing (the same kind and file, or the same value-less kind).</summary>
    /// <param name="other">The other resolution.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    public bool IsSameAs(MentionResolution other) =>
        other is not null
        && string.Equals(Kind, other.Kind, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Resolution.FilePath ?? string.Empty, other.FilePath ?? string.Empty, StringComparison.OrdinalIgnoreCase)
        && (!string.IsNullOrEmpty(Resolution.FilePath) || !MentionSyntax.TakesValue(Kind) || string.Equals(Resolution.Mention, other.Mention, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc/>
    public override string ToString() => DisplayName;
}

using System;

namespace Pillion.VisualStudio.Core.Markdown;

/// <summary>
/// Decides which links in a chat reply may be opened (design doc section 5: links open in the default browser only after
/// a confirmation that shows the URL). Only absolute <c>http</c>, <c>https</c> and <c>mailto</c> URLs qualify; everything
/// else (<c>javascript:</c>, <c>file:</c>, <c>vbscript:</c>, <c>data:</c>, UNC and relative paths, URLs with spaces or
/// control characters) is shown as text and never opened.
/// </summary>
public static class LinkSafety
{
    /// <summary>Gets the safe form of a URL from a reply.</summary>
    /// <param name="url">The URL as the model wrote it.</param>
    /// <param name="uri">The absolute URI to open, when the URL is safe.</param>
    /// <returns><see langword="true"/> when the URL may be offered for opening (after confirmation).</returns>
    public static bool TryGetSafeUri(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var value = url!.Trim();
        foreach (var c in value)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.IsUnc || parsed.IsFile)
        {
            return false;
        }

        var scheme = parsed.Scheme;
        if (string.Equals(scheme, Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase))
        {
            uri = parsed;
            return true;
        }

        if ((string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            && parsed.Host.Length > 0)
        {
            uri = parsed;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Formats a safe URI for the confirmation prompt: the full address, plus the host in its ASCII (punycode) form when
    /// it differs, so a look-alike internationalised host name is visible.
    /// </summary>
    /// <param name="uri">A URI accepted by <see cref="TryGetSafeUri"/>.</param>
    /// <returns>The text to show.</returns>
    public static string Describe(Uri uri)
    {
        if (uri is null)
        {
            throw new ArgumentNullException(nameof(uri));
        }

        var text = uri.AbsoluteUri;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Host, uri.IdnHost, StringComparison.OrdinalIgnoreCase))
        {
            text += Environment.NewLine + string.Format(System.Globalization.CultureInfo.CurrentCulture, ChatStrings.LinkAsciiHostFormat, uri.IdnHost);
        }

        return text;
    }
}

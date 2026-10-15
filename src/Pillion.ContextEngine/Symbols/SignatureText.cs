using System.Text;

namespace Pillion.ContextEngine.Symbols;

/// <summary>Turns a declaration header into the one-line <see cref="SymbolDefinition.Signature"/>.</summary>
public static class SignatureText
{
    /// <summary>The longest signature kept; longer ones end in an ellipsis.</summary>
    public const int MaxLength = 200;

    /// <summary>
    /// Collapses white space (line breaks included) to single spaces, drops leading annotations (<c>@Override</c>),
    /// trims, removes a trailing <c>{</c>, <c>:</c>, <c>=</c>, <c>=&gt;</c> or <c>;</c>, and cuts the result to
    /// <paramref name="maxLength"/> characters.
    /// </summary>
    /// <param name="header">The header text.</param>
    /// <param name="maxLength">The longest result.</param>
    /// <returns>The signature; empty when nothing printable is left.</returns>
    public static string Normalize(ReadOnlySpan<char> header, int maxLength = MaxLength)
    {
        maxLength = Math.Max(maxLength, 2);
        var builder = new StringBuilder(Math.Min(header.Length, maxLength + 1));
        var pendingSpace = false;
        foreach (var c in header)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
            if (builder.Length > maxLength * 2)
            {
                break;
            }
        }

        StripLeadingAnnotations(builder);
        StripTrailing(builder);
        if (builder.Length > maxLength)
        {
            var keep = maxLength - 1;
            if (char.IsHighSurrogate(builder[keep - 1]))
            {
                keep--;
            }

            builder.Length = keep;
            builder.Append('…');
        }

        return builder.ToString();
    }

    /// <summary>
    /// The part of a signature from the name on (<c>Greet(string name)</c> for <c>public string Greet(string name)</c>),
    /// or the name itself when the signature does not contain it as a word.
    /// </summary>
    /// <param name="signature">The signature.</param>
    /// <param name="name">The definition's name.</param>
    /// <returns>The display text.</returns>
    public static string FromName(string signature, string name)
    {
        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(name))
        {
            return name ?? string.Empty;
        }

        var index = 0;
        while ((index = signature.IndexOf(name, index, StringComparison.Ordinal)) >= 0)
        {
            var before = index == 0 || !IsWordChar(signature[index - 1]);
            var end = index + name.Length;
            var after = end == signature.Length || !IsWordChar(signature[end]);
            if (before && after)
            {
                return signature[index..];
            }

            index = end;
        }

        return name;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    private static void StripLeadingAnnotations(StringBuilder builder)
    {
        // Java and TypeScript annotations and decorators that the grammar keeps inside the declaration.
        while (builder.Length > 1 && builder[0] == '@' && (char.IsLetter(builder[1]) || builder[1] == '_'))
        {
            var i = 1;
            while (i < builder.Length && (char.IsLetterOrDigit(builder[i]) || builder[i] is '_' or '.'))
            {
                i++;
            }

            if (i < builder.Length && builder[i] == '(')
            {
                var depth = 0;
                for (; i < builder.Length; i++)
                {
                    if (builder[i] == '(')
                    {
                        depth++;
                    }
                    else if (builder[i] == ')' && --depth == 0)
                    {
                        i++;
                        break;
                    }
                }
            }

            if (i >= builder.Length || builder[i] != ' ')
            {
                return;
            }

            builder.Remove(0, i + 1);
        }
    }

    private static void StripTrailing(StringBuilder builder)
    {
        while (builder.Length > 0)
        {
            var last = builder[^1];
            if (last == ' ' || last == '{' || last == ':' || last == ';' || (last == '=' && !EndsWith(builder, "==")))
            {
                builder.Length--;
            }
            else if (EndsWith(builder, "=>"))
            {
                builder.Length -= 2;
            }
            else
            {
                return;
            }
        }
    }

    private static bool EndsWith(StringBuilder builder, string suffix)
    {
        if (builder.Length < suffix.Length)
        {
            return false;
        }

        for (var i = 0; i < suffix.Length; i++)
        {
            if (builder[builder.Length - suffix.Length + i] != suffix[i])
            {
                return false;
            }
        }

        return true;
    }
}

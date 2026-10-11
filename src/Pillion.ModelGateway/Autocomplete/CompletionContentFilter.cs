using System.Text;

namespace Pillion.ModelGateway;

/// <summary>What the start of a completion turned out to be.</summary>
public enum CompletionContentVerdict
{
    /// <summary>Not known yet: more text is needed.</summary>
    Undecided,

    /// <summary>Code (or anything that is not a reply in prose): passed on.</summary>
    Code,

    /// <summary>A natural-language reply to the prompt ("It looks like you're working on..."): dropped.</summary>
    Prose,

    /// <summary>After a Markdown fence, the model restated code that is already above the caret: dropped.</summary>
    Restated,
}

/// <summary>
/// Keeps chat-style answers out of ghost text, in front of <see cref="CompletionShaper"/> on the gateway side (Gate 1 open
/// issues 1 and 2). An instruct-tuned completion model sometimes answers the prompt instead of continuing it: a sentence
/// such as "It looks like you're encountering an issue with ..." (dropped, the whole completion), or a Markdown fence
/// such as <c>```csharp</c> before the code (the fence line is stripped and the code after it passed on, unless that
/// code restates a line already above the caret, which means the model is quoting the file rather than continuing it).
/// Text is held back only while the start of the completion is undecided, which is a few tokens at most for code: it
/// is released as soon as something code-like appears. Where prose is legitimate (the caret is in a comment or a
/// string, or the document is prose), nothing is held or dropped. Not thread-safe; one per stream.
/// </summary>
public sealed class CompletionContentFilter
{
    /// <summary>The longest undecided start held back, in characters; beyond it the completion is treated as code.</summary>
    public const int MaxHeldCharacters = 160;

    private static readonly string[] ChatOpeners =
    [
        "it", "here", "here's", "sure", "certainly", "i", "i'm", "i'll", "to", "this", "the", "you", "your", "based",
        "unfortunately", "however", "note", "please", "let", "let's", "okay", "ok", "of", "as", "in", "if", "there",
        "these", "that", "we", "my", "sorry", "absolutely", "great", "yes", "no",
    ];

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "it", "it's", "is", "are", "was", "were", "be", "been", "you", "you're", "your", "you've", "i", "i'm",
        "i'll", "we", "this", "that", "these", "those", "there", "here", "here's", "with", "to", "of", "and", "or", "but", "for",
        "on", "at", "by", "from", "about", "like", "looks", "seems", "appears", "can", "could", "would", "should", "will",
        "please", "sure", "provide", "provided", "code", "file", "snippet", "issue", "error", "help", "working", "trying",
        "want", "need", "what", "which", "how", "why", "following", "example", "based",
    };

    private readonly bool _proseAllowed;
    private readonly string[] _prefixLines;
    private readonly string _caretLine;
    private readonly StringBuilder _held = new();
    private bool _fenceStripped;

    /// <summary>Initializes a new instance of the <see cref="CompletionContentFilter"/> class.</summary>
    /// <param name="prefix">The document text before the caret (where prose is allowed; the caret line).</param>
    /// <param name="languageId">The document's language id.</param>
    /// <param name="promptPrefix">Everything the model saw before the caret (header and kept prefix), for restated lines; <paramref name="prefix"/> when <see langword="null"/>.</param>
    public CompletionContentFilter(string prefix, string? languageId, string? promptPrefix = null)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        _proseAllowed = IsProseAllowedAt(prefix, languageId);
        _caretLine = prefix[(prefix.LastIndexOf('\n') + 1)..];
        _prefixLines = (promptPrefix ?? prefix).Split('\n').Select(l => l.Trim()).Where(IsDistinctiveLine).ToArray();
        Verdict = _proseAllowed ? CompletionContentVerdict.Code : CompletionContentVerdict.Undecided;
    }

    /// <summary>Gets what the start of the completion is; <see cref="CompletionContentVerdict.Undecided"/> while text is held back.</summary>
    public CompletionContentVerdict Verdict { get; private set; }

    /// <summary>Gets a value indicating whether the completion is dropped (prose, or restated code after a fence); later pieces return nothing.</summary>
    public bool IsDropped => Verdict is CompletionContentVerdict.Prose or CompletionContentVerdict.Restated;

    /// <summary>Gets a value indicating whether a leading Markdown fence line was stripped.</summary>
    public bool FenceStripped => _fenceStripped;

    /// <summary>Gets a value indicating whether the caret is somewhere prose belongs (nothing is filtered).</summary>
    public bool ProseAllowed => _proseAllowed;

    /// <summary>Gets a value indicating whether a line is a Markdown fence: <c>```</c> or <c>~~~</c>, optionally followed by a language tag.</summary>
    /// <param name="line">The line.</param>
    /// <returns><see langword="true"/> for a fence line.</returns>
    public static bool IsFenceLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var trimmed = line.Trim();
        if (!(trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)))
        {
            return false;
        }

        var tag = trimmed.TrimStart('`', '~');
        return tag.All(c => char.IsLetterOrDigit(c) || c is '+' or '#' or '-' or '_' or '.' or ' ');
    }

    /// <summary>
    /// Classifies the start of a completion (its first line with content), word by word from the start, so a long
    /// sentence is recognised by its opening words alone. Code is recognised early: a first character that is not a
    /// letter, a first word that is not capitalised, or a character prose rarely has (<c>( ) { } [ ] ; = &lt; &gt; " _ :</c>,
    /// a digit, a dot followed by a letter) in the words read so far. Prose needs a usual opening word ("It", "Here",
    /// "Sure", "This" ...), at least three plain words and at least two common English words among them. Eight plain
    /// words without that make it code; so does a whole short line.
    /// </summary>
    /// <param name="text">The completion so far.</param>
    /// <param name="lineComplete">Whether the first line with content is complete (a line break or the end of the stream followed it).</param>
    /// <returns>The verdict.</returns>
    public static CompletionContentVerdict ClassifyStart(string text, bool lineComplete)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        if (start == text.Length)
        {
            return lineComplete ? CompletionContentVerdict.Code : CompletionContentVerdict.Undecided;
        }

        var end = text.IndexOf('\n', start);
        var line = (end < 0 ? text[start..] : text[start..end]).TrimEnd('\r');
        lineComplete |= end >= 0;

        // Prose opens with a capital letter (or "i"); a symbol, digit or lower-case identifier is code.
        var first = line[0];
        if (!char.IsLetter(first) || (char.IsLower(first) && !line.StartsWith("i ", StringComparison.Ordinal) && !line.StartsWith("i'", StringComparison.Ordinal)))
        {
            return CompletionContentVerdict.Code;
        }

        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var complete = lineComplete || line.EndsWith(' ') ? words.Length : words.Length - 1;
        var plain = new List<string>();
        for (var k = 0; k < words.Length; k++)
        {
            // A code character decides even in a word still being written.
            if (HasCodeCharacter(words[k]))
            {
                return CompletionContentVerdict.Code;
            }

            if (k >= complete)
            {
                break;
            }

            var word = words[k].TrimEnd(',', '.', '!', '?', '`', '\'').TrimStart('`', '\'');
            if (word.Length == 0)
            {
                continue;
            }

            if (!word.All(c => char.IsLetter(c) || c is '\'' or '-'))
            {
                return CompletionContentVerdict.Code;
            }

            plain.Add(word);
            if (plain.Count >= 3 && ChatOpeners.Contains(plain[0].ToLowerInvariant()) && plain.Count(Stopwords.Contains) >= 2)
            {
                return CompletionContentVerdict.Prose;
            }

            if (plain.Count >= 8)
            {
                return plain.Count(Stopwords.Contains) >= 4 ? CompletionContentVerdict.Prose : CompletionContentVerdict.Code;
            }
        }

        if (lineComplete)
        {
            // A whole line of plain words with two common English words is prose even without a usual opener.
            return plain.Count >= 4 && plain.Count(Stopwords.Contains) >= 2 ? CompletionContentVerdict.Prose : CompletionContentVerdict.Code;
        }

        return CompletionContentVerdict.Undecided;
    }

    /// <summary>
    /// Gets a value indicating whether prose belongs at the caret: in a prose document (Markdown, plain text), in a line or
    /// block comment, in a docstring, or in a string literal.
    /// </summary>
    /// <param name="prefix">The text before the caret.</param>
    /// <param name="languageId">The language id.</param>
    /// <returns><see langword="true"/> when nothing should be filtered.</returns>
    public static bool IsProseAllowedAt(string prefix, string? languageId)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var language = (languageId ?? string.Empty).Trim().ToLowerInvariant();
        if (language is "markdown" or "md" or "plaintext" or "text" or "txt" or "restructuredtext" or "latex")
        {
            return true;
        }

        var caretLine = prefix[(prefix.LastIndexOf('\n') + 1)..];
        var trimmed = caretLine.TrimStart();
        var hashComments = language is "python" or "py" or "powershell" or "ps1" or "shellscript" or "bash" or "sh" or "ruby" or "rb" or "perl" or "r" or "yaml" or "yml" or "toml";
        if (trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("/*", StringComparison.Ordinal)
            || trimmed.StartsWith("* ", StringComparison.Ordinal)
            || trimmed == "*"
            || trimmed.StartsWith("<!--", StringComparison.Ordinal)
            || (hashComments && trimmed.StartsWith('#'))
            || (language is "vb" or "vbnet" && trimmed.StartsWith('\''))
            || (language is "sql" or "lua" && trimmed.StartsWith("--", StringComparison.Ordinal)))
        {
            return true;
        }

        // A comment that started earlier on the caret line, outside a string.
        if (CommentStartsIn(caretLine, hashComments))
        {
            return true;
        }

        // An unclosed block comment or docstring further up.
        var tail = prefix.Length > 8192 ? prefix[^8192..] : prefix;
        if (tail.LastIndexOf("/*", StringComparison.Ordinal) > tail.LastIndexOf("*/", StringComparison.Ordinal))
        {
            return true;
        }

        if (language is "python" or "py" && (Count(tail, "\"\"\"") % 2 == 1 || Count(tail, "'''") % 2 == 1))
        {
            return true;
        }

        return InString(caretLine);
    }

    /// <summary>Adds a streamed piece.</summary>
    /// <param name="piece">The piece.</param>
    /// <returns>The text that can be passed on now (possibly empty).</returns>
    public string Push(string piece)
    {
        ArgumentNullException.ThrowIfNull(piece);
        if (IsDropped || piece.Length == 0)
        {
            return string.Empty;
        }

        if (Verdict == CompletionContentVerdict.Code)
        {
            return piece;
        }

        _held.Append(piece);
        return Decide(streamEnded: false);
    }

    /// <summary>Ends the stream and returns the text still held back (empty when the completion was dropped).</summary>
    /// <returns>The rest.</returns>
    public string Flush()
    {
        if (IsDropped || Verdict == CompletionContentVerdict.Code)
        {
            var rest = IsDropped ? string.Empty : _held.ToString();
            _held.Clear();
            return rest;
        }

        return Decide(streamEnded: true);
    }

    private static bool IsDistinctiveLine(string line) =>
        line.Length >= 6 && line.Any(char.IsLetter) && !IsFenceLine(line);

    private static int Count(string text, string what)
    {
        var count = 0;
        for (var i = text.IndexOf(what, StringComparison.Ordinal); i >= 0; i = text.IndexOf(what, i + what.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static bool InString(string line)
    {
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'' or '`')
            {
                // A C# char literal or a Python string; an apostrophe in a VB comment was handled above.
                quote = c;
            }
        }

        return quote != '\0';
    }

    private static bool CommentStartsIn(string line, bool hashComments)
    {
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            var next = i + 1 < line.Length ? line[i + 1] : '\0';
            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'' or '`')
            {
                quote = c;
            }
            else if ((c == '/' && next is '/' or '*') || (hashComments && c == '#'))
            {
                return true;
            }
        }

        return false;
    }

    private string Decide(bool streamEnded)
    {
        var text = _held.ToString();

        // A leading fence: wait for its whole line, then strip it (once) and look at what follows.
        if (!_fenceStripped)
        {
            var start = 0;
            while (start < text.Length && char.IsWhiteSpace(text[start]))
            {
                start++;
            }

            var rest = text[start..];
            if (rest.Length > 0 && ("```".StartsWith(rest, StringComparison.Ordinal) || "~~~".StartsWith(rest, StringComparison.Ordinal)) && !streamEnded)
            {
                return string.Empty; // Could still become a fence.
            }

            if (rest.StartsWith("```", StringComparison.Ordinal) || rest.StartsWith("~~~", StringComparison.Ordinal))
            {
                var newline = rest.IndexOf('\n');
                if (newline < 0)
                {
                    if (!streamEnded && rest.Length < MaxHeldCharacters)
                    {
                        return string.Empty;
                    }

                    if (IsFenceLine(rest))
                    {
                        // Nothing but a fence.
                        _fenceStripped = true;
                        _held.Clear();
                        Verdict = CompletionContentVerdict.Code;
                        return string.Empty;
                    }
                }
                else if (IsFenceLine(rest[..newline]))
                {
                    _fenceStripped = true;
                    text = rest[(newline + 1)..];
                    _held.Clear().Append(text);
                }
            }
        }

        if (_fenceStripped)
        {
            return AfterFence(text, streamEnded);
        }

        var verdict = ClassifyStart(text, streamEnded);
        if (verdict == CompletionContentVerdict.Undecided && text.Length > MaxHeldCharacters)
        {
            verdict = CompletionContentVerdict.Code;
        }

        return Settle(verdict, text);
    }

    private string Settle(CompletionContentVerdict verdict, string text)
    {
        switch (verdict)
        {
            case CompletionContentVerdict.Undecided:
                return string.Empty;
            case CompletionContentVerdict.Code:
                Verdict = verdict;
                _held.Clear();
                return text;
            default:
                Verdict = verdict;
                _held.Clear();
                return string.Empty;
        }
    }

    /// <summary>
    /// After a stripped fence the model is quoting code from the start of a line, not continuing at the caret. Once the
    /// first line with content is complete: prose is dropped; a line the model saw above the caret (the header, the file)
    /// means it is restating, so the completion is dropped; when the caret line already has text, the quoted line must
    /// repeat it and only the rest is kept (otherwise it is dropped); at an indented blank caret line the quoted line's
    /// own indentation is removed, since the caret already stands after the caret line's indentation.
    /// </summary>
    private string AfterFence(string text, bool streamEnded)
    {
        var offset = 0;
        while (true)
        {
            var newline = text.IndexOf('\n', offset);
            if (newline < 0 && !streamEnded)
            {
                return text.Length > MaxHeldCharacters ? Settle(CompletionContentVerdict.Restated, text) : string.Empty;
            }

            var lineEnd = newline < 0 ? text.Length : newline;
            var line = text[offset..lineEnd].TrimEnd('\r');
            if (line.Trim().Length == 0)
            {
                if (newline < 0)
                {
                    return Settle(CompletionContentVerdict.Code, string.Empty);
                }

                offset = newline + 1;
                continue;
            }

            if (ClassifyStart(line, lineComplete: true) == CompletionContentVerdict.Prose)
            {
                return Settle(CompletionContentVerdict.Prose, text);
            }

            var trimmed = line.Trim();
            if (IsDistinctiveLine(trimmed) && _prefixLines.Contains(trimmed, StringComparer.Ordinal))
            {
                return Settle(CompletionContentVerdict.Restated, text);
            }

            var rest = text[lineEnd..];
            var caret = _caretLine.Trim();
            if (caret.Length > 0)
            {
                var quoted = line.TrimStart();
                if (!quoted.StartsWith(caret, StringComparison.Ordinal))
                {
                    return Settle(CompletionContentVerdict.Restated, text);
                }

                // The caret line ends with a space ("return ") and the quoted line goes on after it: drop the repeated space.
                var continued = quoted[caret.Length..];
                return Settle(CompletionContentVerdict.Code, (_caretLine.EndsWith(' ') ? continued.TrimStart(' ') : continued) + rest);
            }

            return Settle(CompletionContentVerdict.Code, line.TrimStart() + rest);
        }
    }

    private static bool HasCodeCharacter(string word)
    {
        for (var i = 0; i < word.Length; i++)
        {
            var c = word[i];
            if (c is '(' or ')' or '{' or '}' or '[' or ']' or ';' or '=' or '<' or '>' or '"' or '_' or ':' or '|' or '&' or '$' or '@' or '\\' or '/' or '*' or '+' or '#'
                || char.IsDigit(c)
                || (c == '.' && i + 1 < word.Length && char.IsLetter(word[i + 1])))
            {
                return true;
            }
        }

        return false;
    }
}

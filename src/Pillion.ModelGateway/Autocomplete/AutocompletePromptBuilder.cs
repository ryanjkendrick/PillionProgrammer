using System.Text;
using Pillion.Common.Hardware;
using Pillion.Contracts;

namespace Pillion.ModelGateway;

/// <summary>
/// The autocomplete prompt budget (design section 7): at most <see cref="CpuPromptTokens"/> prompt tokens when the
/// completion model runs on the CPU and <see cref="GpuPromptTokens"/> on a GPU or in the cloud, prefix, suffix, header and
/// FIM framing included.
/// </summary>
/// <remarks>
/// Shares of the budget left after the FIM framing: the suffix is guaranteed <see cref="SuffixMinimumShare"/> (15%) and
/// the prefix <see cref="PrefixMinimumShare"/> (60%) when they need it; the header may then take up to
/// <see cref="HeaderMaximumShare"/> (15%) of what is left; whatever remains goes to the prefix first and to the suffix last,
/// so the suffix is the first to be trimmed.
/// </remarks>
public static class AutocompleteBudget
{
    /// <summary>The prompt budget when the completion model runs on the CPU.</summary>
    public const int CpuPromptTokens = 1024;

    /// <summary>The prompt budget on a GPU or in the cloud.</summary>
    public const int GpuPromptTokens = 2048;

    /// <summary>The share of the budget the suffix keeps when it needs it, before the prefix and header are served.</summary>
    public const double SuffixMinimumShare = 0.15;

    /// <summary>The share of the budget the prefix keeps when it needs it, before the header is served.</summary>
    public const double PrefixMinimumShare = 0.60;

    /// <summary>The most the header may take, and only out of what the minimum shares leave.</summary>
    public const double HeaderMaximumShare = 0.15;

    /// <summary>The tokens reserved for a FIM template the server applies itself (Ollama): three FIM tokens and a BOS token, rounded up.</summary>
    public const int ServerTemplateReserveTokens = 8;

    /// <summary>The most lines a multi-line completion may have.</summary>
    public const int MultiLineMaxLines = 30;

    /// <summary>Gets the prompt budget for a model.</summary>
    /// <param name="isLocal">Whether the model runs on this machine.</param>
    /// <param name="tier">The GPU tier; a local model on a <see cref="GpuTier.CpuOnly"/> machine runs on the CPU.</param>
    /// <returns><see cref="CpuPromptTokens"/> or <see cref="GpuPromptTokens"/>.</returns>
    public static int PromptTokensFor(bool isLocal, GpuTier tier) =>
        isLocal && tier == GpuTier.CpuOnly ? CpuPromptTokens : GpuPromptTokens;
}

/// <summary>The prompt the gateway sends for one autocomplete request, after the budget was applied.</summary>
public sealed record AutocompletePrompt
{
    /// <summary>Gets the header (comment lines that end with a newline), or an empty string.</summary>
    public string Header { get; init; } = string.Empty;

    /// <summary>Gets the kept prefix (a whole-line tail of the request's prefix, ending at the caret).</summary>
    public string Prefix { get; init; } = string.Empty;

    /// <summary>Gets the kept suffix (a whole-line head of the request's suffix, starting at the caret).</summary>
    public string Suffix { get; init; } = string.Empty;

    /// <summary>Gets the text that goes before the caret: the header followed by the prefix.</summary>
    public string PromptPrefix => Header + Prefix;

    /// <summary>Gets the budget the prompt was built for.</summary>
    public int Budget { get; init; }

    /// <summary>Gets the tokens of the whole prompt: header, prefix, suffix and FIM framing.</summary>
    public int PromptTokens => HeaderTokens + PrefixTokens + SuffixTokens + TemplateTokens;

    /// <summary>Gets the header's tokens.</summary>
    public int HeaderTokens { get; init; }

    /// <summary>Gets the kept prefix's tokens.</summary>
    public int PrefixTokens { get; init; }

    /// <summary>Gets the kept suffix's tokens.</summary>
    public int SuffixTokens { get; init; }

    /// <summary>Gets the tokens of the FIM framing (counted for an explicit template, reserved for a server-applied one).</summary>
    public int TemplateTokens { get; init; }

    /// <summary>Gets a value indicating whether the counts come from the model's tokenizer rather than an estimate.</summary>
    public bool IsExact { get; init; }

    /// <summary>Gets the number of whole prefix lines dropped from the top.</summary>
    public int PrefixLinesDropped { get; init; }

    /// <summary>Gets the number of whole suffix lines dropped from the bottom.</summary>
    public int SuffixLinesDropped { get; init; }

    /// <summary>Gets a value indicating whether the caret line itself had to be cut (a line longer than the prefix budget).</summary>
    public bool CaretLineCut { get; init; }

    /// <summary>Gets the header items included, in layout order (<c>file</c>, <c>openDocuments</c>, <c>declaration</c>, <c>recentEdit</c>).</summary>
    public IReadOnlyList<string> HeaderItems { get; init; } = [];
}

/// <summary>
/// Builds the autocomplete prompt within the budget (design section 7, Autocomplete prompt budget). No project or user
/// rules are ever added. The layout keeps consecutive keystrokes from changing anything but the tail, so a resident local
/// model reuses its cached prompt prefix: the header comes first and changes only when the declaration, the open
/// documents or the edits elsewhere change; the prefix starts on a line whose absolute number is a multiple of
/// <see cref="AnchorLines"/>, so the start moves only in steps of that many lines as the file grows.
/// </summary>
public static class AutocompletePromptBuilder
{
    /// <summary>The line step the prefix start is aligned to, so it stays put while the user types.</summary>
    public const int AnchorLines = 16;

    /// <summary>The most open documents the header names.</summary>
    public const int MaxOpenDocuments = 8;

    /// <summary>The most recent edits the header lists.</summary>
    public const int MaxRecentEdits = 3;

    /// <summary>The longest declaration or edit text kept in the header, in characters.</summary>
    public const int MaxHeaderItemLength = 160;

    // No tokenizer turns code into tokens longer than this on average, so text further from the caret is never counted.
    private const int MaxCharsPerToken = 16;

    /// <summary>Builds the prompt for a request.</summary>
    /// <param name="request">The completion request.</param>
    /// <param name="budget">The prompt budget in tokens.</param>
    /// <param name="counter">The model's token counter.</param>
    /// <param name="explicitFormat">The FIM template when the gateway frames the prompt itself; <see langword="null"/> when the server applies the model's template.</param>
    /// <returns>The prompt.</returns>
    public static AutocompletePrompt Build(CompletionRequest request, int budget, IModelTokenCounter counter, FimFormat? explicitFormat = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(counter);

        var prefix = request.Prefix ?? string.Empty;
        var suffix = request.Suffix ?? string.Empty;
        var template = explicitFormat is null ? AutocompleteBudget.ServerTemplateReserveTokens : counter.Count(explicitFormat.Render(string.Empty, string.Empty));
        var available = Math.Max(0, budget - template);

        var suffixMinimum = (int)(available * AutocompleteBudget.SuffixMinimumShare);
        var prefixMinimum = (int)(available * AutocompleteBudget.PrefixMinimumShare);
        var headerMaximum = (int)(available * AutocompleteBudget.HeaderMaximumShare);

        // Only text that could possibly fit is counted.
        var prefixWindow = PrefixWindow(prefix, available);
        var suffixWindow = SuffixWindow(suffix, available);
        var prefixNeed = counter.Count(prefixWindow);
        var suffixNeed = counter.Count(suffixWindow);

        var suffixFloor = Math.Min(suffixNeed, suffixMinimum);
        var prefixFloor = Math.Min(prefixNeed, prefixMinimum);
        var headerRoom = Math.Min(headerMaximum, available - suffixFloor - prefixFloor);

        var context = new HeaderContext(request);
        var header = BuildHeader(context, headerRoom, counter, includeDeclaration: true);
        var layout = Layout(request, prefix, suffix, available, header.Tokens, suffixFloor, prefixNeed, suffixNeed, counter);

        // The declaration is left out when the kept prefix already shows it. A smaller header only gives the prefix more
        // room, so the declaration stays visible after the rebuild.
        if (header.Items.Contains("declaration") && context.Declaration is { } declaration && WithoutWhitespace(layout.Prefix).Contains(WithoutWhitespace(declaration), StringComparison.Ordinal))
        {
            header = BuildHeader(context, headerRoom, counter, includeDeclaration: false);
            layout = Layout(request, prefix, suffix, available, header.Tokens, suffixFloor, prefixNeed, suffixNeed, counter);
        }

        return new AutocompletePrompt
        {
            Header = header.Text,
            HeaderItems = header.Items,
            HeaderTokens = header.Tokens,
            Prefix = layout.Prefix,
            PrefixTokens = layout.PrefixTokens,
            PrefixLinesDropped = layout.PrefixLinesDropped,
            CaretLineCut = layout.CaretLineCut,
            Suffix = layout.Suffix,
            SuffixTokens = layout.SuffixTokens,
            SuffixLinesDropped = layout.SuffixLinesDropped,
            TemplateTokens = template,
            Budget = budget,
            IsExact = counter.IsExact,
        };
    }

    /// <summary>Gets the line comment markers for a language, or <see langword="null"/> when it has none (no header is written).</summary>
    /// <param name="languageId">The language id (for example <c>csharp</c>).</param>
    /// <param name="filePath">The file path, used when the language id is empty.</param>
    /// <returns>The opening and closing markers.</returns>
    public static (string Open, string Close)? CommentSyntax(string? languageId, string? filePath)
    {
        var language = (languageId ?? string.Empty).Trim().ToLowerInvariant();
        if (language.Length == 0 && !string.IsNullOrEmpty(filePath))
        {
            language = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        }

        return language switch
        {
            "python" or "py" or "powershell" or "ps1" or "psm1" or "shellscript" or "bash" or "sh" or "ruby" or "rb" or "perl" or "r" or "yaml" or "yml" or "toml" or "dockerfile" or "makefile" or "cmake" => ("# ", string.Empty),
            "sql" or "lua" or "haskell" or "hs" => ("-- ", string.Empty),
            "html" or "xml" or "xaml" or "razor" or "cshtml" or "vue" or "svelte" or "csproj" or "props" or "targets" => ("<!-- ", " -->"),
            "css" => ("/* ", " */"),
            "vb" or "vbnet" => ("' ", string.Empty),
            "bat" or "cmd" => ("REM ", string.Empty),
            "json" or "jsonc" or "markdown" or "md" or "plaintext" or "text" or "txt" => null,
            _ => ("// ", string.Empty),
        };
    }

    private static PromptLayout Layout(CompletionRequest request, string prefix, string suffix, int available, int headerTokens, int suffixFloor, int prefixNeed, int suffixNeed, IModelTokenCounter counter)
    {
        var rest = available - headerTokens;
        var prefixAllowance = Math.Max(0, Math.Min(prefixNeed, rest - suffixFloor));
        var kept = TrimPrefix(request, prefix, prefixAllowance, counter);

        // The suffix gets what the prefix (after its line-aligned trim) left over.
        var suffixAllowance = Math.Max(0, Math.Min(suffixNeed, rest - kept.Tokens));
        var keptSuffix = TrimSuffix(suffix, suffixAllowance, counter);
        return new PromptLayout(kept.Text, kept.Tokens, kept.LinesDropped, kept.CaretLineCut, keptSuffix.Text, keptSuffix.Tokens, keptSuffix.LinesDropped);
    }

    private static (string Text, int Tokens, int LinesDropped, bool CaretLineCut) TrimPrefix(CompletionRequest request, string prefix, int allowance, IModelTokenCounter counter)
    {
        if (prefix.Length == 0 || allowance <= 0)
        {
            return (string.Empty, 0, prefix.Length == 0 ? 0 : LineStarts(prefix).Count - 1, prefix.Length > 0);
        }

        var starts = LineStarts(prefix);
        var whole = prefix.Length <= allowance * MaxCharsPerToken ? counter.Count(prefix) : int.MaxValue;
        if (whole <= allowance)
        {
            return (prefix, whole, 0, false);
        }

        // Candidate starts: lines whose absolute number is a multiple of AnchorLines, then the caret line itself. The
        // absolute number of the prefix's first line follows from the caret line; when the caret line is not known (or
        // inconsistent), the prefix is taken to start the document.
        var firstLine = Math.Max(0, request.CaretLine - (starts.Count - 1));
        var minimumStart = prefix.Length - ((long)allowance * MaxCharsPerToken);
        var candidates = new List<int>();
        for (var i = 1; i < starts.Count - 1; i++)
        {
            if ((firstLine + i) % AnchorLines == 0 && starts[i] >= minimumStart)
            {
                candidates.Add(i);
            }
        }

        candidates.Add(starts.Count - 1);

        // Token counts fall as the start moves down, so the first candidate that fits is found by binary search.
        int low = 0, high = candidates.Count - 1, found = -1, foundTokens = 0;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var tokens = counter.Count(prefix[starts[candidates[mid]]..]);
            if (tokens <= allowance)
            {
                found = mid;
                foundTokens = tokens;
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }

        if (found >= 0)
        {
            var line = candidates[found];
            return (prefix[starts[line]..], foundTokens, line, false);
        }

        // Even the caret line is too long (minified code): keep its tail.
        var caretLine = prefix[starts[^1]..];
        var tail = CutToFit(caretLine, allowance, counter, keepEnd: true);
        return (tail, counter.Count(tail), starts.Count - 1, true);
    }

    private static (string Text, int Tokens, int LinesDropped) TrimSuffix(string suffix, int allowance, IModelTokenCounter counter)
    {
        if (suffix.Length == 0 || allowance <= 0)
        {
            return (string.Empty, 0, suffix.Length == 0 ? 0 : LineStarts(suffix).Count);
        }

        var whole = suffix.Length <= allowance * MaxCharsPerToken ? counter.Count(suffix) : int.MaxValue;
        if (whole <= allowance)
        {
            return (suffix, whole, 0);
        }

        // Ends after each line (including its newline); the most lines that fit are kept.
        var starts = LineStarts(suffix);
        var ends = starts.Skip(1).Append(suffix.Length).Where(e => e <= (long)allowance * MaxCharsPerToken).ToList();
        int low = 0, high = ends.Count - 1, found = -1, foundTokens = 0;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var tokens = counter.Count(suffix[..ends[mid]]);
            if (tokens <= allowance)
            {
                found = mid;
                foundTokens = tokens;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (found >= 0)
        {
            return (suffix[..ends[found]], foundTokens, starts.Count - (found + 1));
        }

        var firstLine = suffix[..(starts.Count > 1 ? starts[1] : suffix.Length)];
        var head = CutToFit(firstLine, allowance, counter, keepEnd: false);
        return (head, counter.Count(head), starts.Count - 1);
    }

    private static string CutToFit(string text, int allowance, IModelTokenCounter counter, bool keepEnd)
    {
        int low = 0, high = Math.Min(text.Length, allowance * MaxCharsPerToken), best = 0;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var candidate = keepEnd ? text[^mid..] : text[..mid];
            if (counter.Count(candidate) <= allowance)
            {
                best = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return keepEnd ? text[^best..] : text[..best];
    }

    private static string PrefixWindow(string prefix, int available)
    {
        var limit = (long)available * MaxCharsPerToken;
        return prefix.Length <= limit ? prefix : prefix[(int)(prefix.Length - limit)..];
    }

    private static string SuffixWindow(string suffix, int available)
    {
        var limit = (long)available * MaxCharsPerToken;
        return suffix.Length <= limit ? suffix : suffix[..(int)limit];
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        // A trailing newline starts an empty last line, which is where the caret is.
        return starts;
    }

    private static (string Text, int Tokens, IReadOnlyList<string> Items) BuildHeader(HeaderContext context, int room, IModelTokenCounter counter, bool includeDeclaration)
    {
        if (room <= 0 || context.Comment is not { } comment)
        {
            return (string.Empty, 0, []);
        }

        string Line(string text) => comment.Open + text + comment.Close + "\n";

        // Inclusion priority: file, declaration, open documents, recent edits. Layout order is most to least stable.
        var file = context.FileName is { } name ? Line("File: " + name) : null;
        var declaration = includeDeclaration && context.Declaration is { } d ? Line("In: " + d) : null;
        string? openDocuments = null;
        var edits = new List<string>();

        string Compose() => (file ?? string.Empty) + (openDocuments ?? string.Empty) + (declaration ?? string.Empty) + string.Concat(edits);
        bool Fits() => counter.Count(Compose()) <= room;

        if (file is not null && !Fits())
        {
            return (string.Empty, 0, []);
        }

        if (declaration is not null && !Fits())
        {
            declaration = null;
        }

        for (var count = Math.Min(MaxOpenDocuments, context.OpenDocuments.Count); count > 0; count /= 2)
        {
            openDocuments = Line("Open files: " + string.Join(", ", context.OpenDocuments.Take(count).Order(StringComparer.OrdinalIgnoreCase)));
            if (Fits())
            {
                break;
            }

            openDocuments = null;
        }

        foreach (var edit in context.RecentEdits)
        {
            edits.Add(Line("Recent edit: " + edit));
            if (!Fits())
            {
                edits.RemoveAt(edits.Count - 1);
                break;
            }
        }

        var text = Compose();
        if (text.Length == 0)
        {
            return (string.Empty, 0, []);
        }

        var items = new List<string>();
        if (file is not null)
        {
            items.Add("file");
        }

        if (openDocuments is not null)
        {
            items.Add("openDocuments");
        }

        if (declaration is not null)
        {
            items.Add("declaration");
        }

        items.AddRange(edits.Select(_ => "recentEdit"));
        return (text, counter.Count(text), items);
    }

    private static string WithoutWhitespace(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    private static string Flatten(string text)
    {
        var builder = new StringBuilder(Math.Min(text.Length, MaxHeaderItemLength));
        var space = false;
        foreach (var c in text.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space && builder.Length > 0)
            {
                builder.Append(' ');
            }

            space = false;
            builder.Append(c);
            if (builder.Length >= MaxHeaderItemLength)
            {
                break;
            }
        }

        return builder.ToString();
    }

    /// <summary>What the header may say, normalised once per request.</summary>
    private sealed class HeaderContext
    {
        public HeaderContext(CompletionRequest request)
        {
            Comment = CommentSyntax(request.LanguageId, request.FilePath);
            FileName = string.IsNullOrWhiteSpace(request.FilePath) ? null : Path.GetFileName(request.FilePath.Trim());
            var declaration = string.IsNullOrWhiteSpace(request.EnclosingDeclaration) ? null : Flatten(request.EnclosingDeclaration);
            Declaration = string.IsNullOrEmpty(declaration) ? null : declaration;

            // Names only (never full paths), the current file left out, most recently used first; the list shown is
            // then sorted so a change of focus between the same files does not change the header.
            OpenDocuments = (request.OpenDocuments ?? [])
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => Path.GetFileName(p.Trim()))
                .Where(n => n.Length > 0 && !string.Equals(n, FileName, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Edits in the current file are already visible around the caret, and listing them would change the header
            // on every keystroke and defeat the model's prompt cache, so only edits elsewhere are kept.
            RecentEdits = (request.RecentEdits ?? [])
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Where(e => FileName is null || !e.Contains(FileName, StringComparison.OrdinalIgnoreCase))
                .Select(Flatten)
                .Take(MaxRecentEdits)
                .ToList();
        }

        public (string Open, string Close)? Comment { get; }

        public string? FileName { get; }

        public string? Declaration { get; }

        public List<string> OpenDocuments { get; }

        public List<string> RecentEdits { get; }
    }

    private sealed record PromptLayout(string Prefix, int PrefixTokens, int PrefixLinesDropped, bool CaretLineCut, string Suffix, int SuffixTokens, int SuffixLinesDropped);
}

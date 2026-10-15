using System.Globalization;
using System.Text.RegularExpressions;
using Pillion.Contracts;
using Pillion.Host.Context.Mentions;

namespace Pillion.Host.Context;

/// <summary>
/// <c>@git</c>, <c>@output</c>, <c>@build</c> and <c>@tests</c> (work item 2.13). <c>@git</c> reads the repository through
/// <see cref="GitMentions"/>. The other three come from Visual Studio: the VSIX sends the Output pane's tail, the last build's
/// output or the last test results as the payload (contract 1.3, <see cref="MentionKinds"/>), and the host keeps their end
/// within the token limit and cites the files and lines the diagnostics and stack traces name.
/// </summary>
internal sealed partial class ContextRpcService
{
    /// <summary>The Output panes <c>@output</c> names, by argument.</summary>
    private static readonly Dictionary<string, string> PaneNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["build"] = "Build",
        ["debug"] = "Debug",
        ["tests"] = "Tests",
        ["test"] = "Tests",
    };

    /// <summary>The pane <c>@output</c> reads: the first argument, else the value, else Build.</summary>
    /// <param name="request">The request.</param>
    /// <param name="value">The value after <c>@output:</c>.</param>
    /// <returns>The pane's display name.</returns>
    internal static string OutputPane(MentionRequest request, string value)
    {
        var name = request.Arguments.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))?.Trim() ?? value.Trim();
        return name.Length == 0 ? "Build" : PaneNames.TryGetValue(name, out var known) ? known : name;
    }

    /// <summary>The <c>file:line</c> spans a build log or test output names (MSBuild diagnostics and .NET stack frames).</summary>
    /// <param name="text">The text.</param>
    /// <returns>Up to <see cref="MaxCitations"/> citations, in order of appearance, without duplicates.</returns>
    internal static Citation[] SignalCitations(string text)
    {
        var citations = new List<Citation>();
        var seen = new HashSet<(string, int)>();
        foreach (var match in MsBuildDiagnostic().Matches(text).Concat(StackFrame().Matches(text)).OrderBy(m => m.Index))
        {
            var path = match.Groups["file"].Value.Trim();
            if (!int.TryParse(match.Groups["line"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var line) || line < 1)
            {
                continue;
            }

            if (seen.Add((path.ToUpperInvariant(), line)))
            {
                citations.Add(new Citation { FilePath = path, StartLine = line - 1, EndLine = line - 1 });
                if (citations.Count >= MaxCitations)
                {
                    break;
                }
            }
        }

        return [.. citations];
    }

    private static async Task<MentionResolution> ResolveGitAsync(Resolution context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var git = GitMentions.Parse(request.Arguments, context.Parsed.Value);
        var result = await Task.Run(() => GitMentions.Read(git, request.WorkspaceRoots, context.Policy, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (!result.IsOk)
        {
            return context.Unresolved(MentionKinds.Git, result.Reason);
        }

        return context.Resolved(MentionKinds.Git, result.DisplayName, null, result.Text, result.Cut, [.. result.Citations]);
    }

    private static MentionResolution ResolveOutput(Resolution context)
    {
        var pane = OutputPane(context.Request, context.Parsed.Value);
        var payload = context.Request.Payload;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return context.Unresolved(MentionKinds.Output, $"The {pane} pane of the Output window is empty or not open.");
        }

        return context.ResolvedTail(MentionKinds.Output, "Output: " + pane, payload, SignalCitations(payload));
    }

    private static MentionResolution ResolveBuild(Resolution context)
    {
        var payload = context.Request.Payload;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return context.Unresolved(MentionKinds.Build, "No build has finished since Visual Studio started, so there is no build output to pin.");
        }

        var name = payload.Contains("Build failed", StringComparison.OrdinalIgnoreCase) || payload.Contains("build: failed", StringComparison.OrdinalIgnoreCase)
            ? "Last build (failed)"
            : payload.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase) || payload.Contains("build: succeeded", StringComparison.OrdinalIgnoreCase)
                ? "Last build (succeeded)"
                : "Last build";
        return context.ResolvedTail(MentionKinds.Build, name, payload, SignalCitations(payload));
    }

    private static MentionResolution ResolveTests(Resolution context)
    {
        var payload = context.Request.Payload;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return context.Unresolved(MentionKinds.Tests, "There are no test results to pin: run the tests, then pin @tests again.");
        }

        return context.ResolvedTail(MentionKinds.Tests, "Tests", payload, SignalCitations(payload));
    }

    [GeneratedRegex(@"^\s*(?:\d+>)?(?<file>[A-Za-z]:\\[^(\r\n]+?)\((?<line>\d+)(?:,\d+){0,3}\)\s*:\s*(?:error|warning)\b", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex MsBuildDiagnostic();

    [GeneratedRegex(@"\bin (?<file>[A-Za-z]:\\[^\r\n:]+):line (?<line>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex StackFrame();
}

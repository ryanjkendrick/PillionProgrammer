using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.Host.Rpc;
using StreamJsonRpc;

namespace Pillion.Host.WorkspaceQueries;

/// <summary>
/// The host's <see cref="IWorkspaceQueries"/> (work item 2.14): asks Visual Studio's live Roslyn workspace for C# and
/// Visual Basic, and falls back to the index's approximate edges otherwise.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Visual Studio is asked when <c>features.workspaceQueries</c> is on, it is connected, and the file is C# or Visual
/// Basic (or unknown: Roslyn then searches the solution by name). It enforces its budgets (one in flight, 2 s, five a
/// second) and refuses at once over them; the host waits at most <see cref="CallTimeout"/> for an answer.</item>
/// <item>Any refusal, error or timeout, a symbol Roslyn does not find, and another language send the query to the index;
/// the reason is in <see cref="WorkspaceQueryResult.FallbackReason"/> and the log. Only the caller's own cancellation
/// propagates.</item>
/// <item>A Roslyn answer cut short by the 2 s budget is completed from the index: the extra sites are marked approximate
/// and the answer stays <see cref="WorkspaceQueryResult.Truncated"/>. <see cref="FindReferencesResponse"/> has no truncated
/// flag in contract 1.3, so a find-references answer that took the whole budget counts as cut short.</item>
/// <item>Find-references needs a location in contract 1.3: the column of the name on the requested line, or, without a
/// file, the declaration the index knows.</item>
/// </list>
/// </remarks>
internal sealed partial class WorkspaceQueries : IWorkspaceQueries
{
    /// <summary>Visual Studio's budget for one query (the contract's 2 s).</summary>
    public static readonly TimeSpan VsixBudget = TimeSpan.FromSeconds(2);

    /// <summary>How long the host waits for Visual Studio's answer: the budget plus time for the round trip.</summary>
    public static readonly TimeSpan CallTimeout = VsixBudget + TimeSpan.FromSeconds(1);

    /// <summary>The sites returned when the query asks for 0.</summary>
    public const int DefaultMaxResults = 100;

    /// <summary>The most sites one query may ask for.</summary>
    public const int MaxResultsLimit = 1000;

    /// <summary>The references Visual Studio returns at most for one find-references query.</summary>
    public const int VsixMaxReferences = 500;

    private readonly IVsixConnection _vsix;
    private readonly IFeatureFlags _flags;
    private readonly ISymbolGraphProvider _graphs;
    private readonly ILogger<WorkspaceQueries> _logger;
    private readonly TimeSpan _callTimeout;
    private readonly TimeSpan _budget;

    /// <summary>Initializes a new instance of the <see cref="WorkspaceQueries"/> class.</summary>
    /// <param name="vsix">The connection to Visual Studio.</param>
    /// <param name="flags">The feature flags.</param>
    /// <param name="graphs">The index's symbol graphs.</param>
    /// <param name="logger">The logger.</param>
    public WorkspaceQueries(IVsixConnection vsix, IFeatureFlags flags, ISymbolGraphProvider graphs, ILogger<WorkspaceQueries> logger)
        : this(vsix, flags, graphs, logger, CallTimeout, VsixBudget)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="WorkspaceQueries"/> class with other timings (tests).</summary>
    /// <param name="vsix">The connection to Visual Studio.</param>
    /// <param name="flags">The feature flags.</param>
    /// <param name="graphs">The index's symbol graphs.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="callTimeout">How long to wait for Visual Studio.</param>
    /// <param name="budget">Visual Studio's budget, for the find-references truncation rule.</param>
    internal WorkspaceQueries(IVsixConnection vsix, IFeatureFlags flags, ISymbolGraphProvider graphs, ILogger<WorkspaceQueries> logger, TimeSpan callTimeout, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(vsix);
        ArgumentNullException.ThrowIfNull(flags);
        ArgumentNullException.ThrowIfNull(graphs);
        ArgumentNullException.ThrowIfNull(logger);
        _vsix = vsix;
        _flags = flags;
        _graphs = graphs;
        _logger = logger;
        _callTimeout = callTimeout;
        _budget = budget;
    }

    /// <summary>Whether a file is answered by Roslyn: C# and Visual Basic sources.</summary>
    /// <param name="path">The file, or empty when unknown.</param>
    /// <returns>True for <c>.cs</c> and <c>.vb</c> files.</returns>
    public static bool IsRoslynLanguage(string path) =>
        Path.GetExtension(path) is { } extension
        && (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) || extension.Equals(".vb", StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc/>
    public async Task<WorkspaceQueryResult> CallersOfAsync(WorkspaceSymbolQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var started = Stopwatch.GetTimestamp();
        var max = MaxResults(query);
        var lines = new SourceLineCache();
        if (string.IsNullOrWhiteSpace(query.Name))
        {
            return new WorkspaceQueryResult(false, [], false, WorkspaceQuerySource.None, "no symbol name", Stopwatch.GetElapsedTime(started));
        }

        var reason = WhyNotRoslyn(query.FilePath);
        if (reason is null)
        {
            try
            {
                var response = await CallVsixAsync(
                    (service, token) => service.CallersOfAsync(
                        new CallersOfRequest { SymbolName = query.Name, Container = query.Container, FilePath = query.FilePath, Line = query.Line, MaxResults = max },
                        token),
                    cancellationToken).ConfigureAwait(false);
                if (response.IsFound)
                {
                    var sites = response.Callers.Select(c => new WorkspaceSite(c.FilePath, c.Line, c.Snippet, c.CallerName, c.IsApproximate)).ToList();
                    return await FinishRoslynAsync("callers_of", query, sites, response.Truncated, callsOnly: true, max, lines, started, cancellationToken).ConfigureAwait(false);
                }

                reason = "Roslyn did not find the symbol";
            }
#pragma warning disable CA1031 // Every failure of Visual Studio's side sends the query to the index; only cancellation propagates.
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
            {
                reason = Describe(ex);
            }
        }

        return await FromIndexAsync("callers_of", query, callsOnly: true, max, reason, lines, started, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<WorkspaceQueryResult> FindReferencesAsync(WorkspaceSymbolQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var started = Stopwatch.GetTimestamp();
        var max = MaxResults(query);
        var lines = new SourceLineCache();
        if (string.IsNullOrWhiteSpace(query.Name) && (string.IsNullOrEmpty(query.FilePath) || query.Line < 0))
        {
            return new WorkspaceQueryResult(false, [], false, WorkspaceQuerySource.None, "no symbol name or location", Stopwatch.GetElapsedTime(started));
        }

        var location = await LocateAsync(query, lines, cancellationToken).ConfigureAwait(false);
        var reason = location is null ? "the symbol's declaration is not known (no file and line, and not in the index)" : WhyNotRoslyn(location.FilePath);
        if (reason is null)
        {
            try
            {
                var asked = Stopwatch.GetTimestamp();
                var response = await CallVsixAsync(
                    (service, token) => service.FindReferencesAsync(new FindReferencesRequest { Location = location! }, token),
                    cancellationToken).ConfigureAwait(false);
                var took = Stopwatch.GetElapsedTime(asked);
                var references = response.References ?? [];
                if (references.Count > 0)
                {
                    var sites = references.Select(r => new WorkspaceSite(r.FilePath, r.Line, lines.Snippet(r.FilePath, r.Line), string.Empty, IsApproximate: false)).ToList();

                    // Contract 1.3 cannot say a find-references answer is partial: one that took the whole budget, or hit
                    // Visual Studio's cap, may be.
                    var truncated = took >= _budget || references.Count >= VsixMaxReferences;
                    return await FinishRoslynAsync("find_references", query, sites, truncated, callsOnly: false, max, lines, started, cancellationToken).ConfigureAwait(false);
                }

                reason = "Roslyn found no references";
            }
#pragma warning disable CA1031 // Every failure of Visual Studio's side sends the query to the index; only cancellation propagates.
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
            {
                reason = Describe(ex);
            }
        }

        return await FromIndexAsync("find_references", query, callsOnly: false, max, reason, lines, started, cancellationToken).ConfigureAwait(false);
    }

    private static int MaxResults(WorkspaceSymbolQuery query) =>
        query.MaxResults <= 0 ? DefaultMaxResults : Math.Min(query.MaxResults, MaxResultsLimit);

    private static string Describe(Exception exception) => exception switch
    {
        PillionRpcException pillion => $"Visual Studio is not available ({pillion.Error.Code}: {pillion.Error.Message})",
        RemoteMethodNotFoundException => "Visual Studio does not answer this query (an older extension)",
        RemoteInvocationException remote => $"Visual Studio refused the query ({remote.Message})",
        ConnectionLostException => "the connection to Visual Studio was lost",
        OperationCanceledException => "Visual Studio did not answer in time",
        _ => $"the query failed ({exception.GetType().Name}: {exception.Message})",
    };

    /// <summary>Why Visual Studio is not asked for a file, or <see langword="null"/> when it is.</summary>
    private string? WhyNotRoslyn(string path)
    {
        bool enabled;
        try
        {
            enabled = _flags.IsEnabled(FeatureNames.WorkspaceQueries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return "the settings could not be read (" + ex.GetType().Name + ")";
        }

        if (!enabled)
        {
            return "features.workspaceQueries is off";
        }

        if (!string.IsNullOrEmpty(path) && !IsRoslynLanguage(path))
        {
            return "the language is not C# or Visual Basic";
        }

        return _vsix.IsConnected ? null : "Visual Studio is not connected";
    }

    /// <summary>Calls Visual Studio with the host's timeout; a fresh proxy each time (a reconnect replaces it).</summary>
    private async Task<T> CallVsixAsync<T>(Func<IWorkspaceQueryService, CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_callTimeout);
        var service = _vsix.GetWorkspaceQueryService();
        return await call(service, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
    }

    /// <summary>The location find-references asks Visual Studio about: the requested line, or the index's declaration.</summary>
    private async Task<SourceLocation?> LocateAsync(WorkspaceSymbolQuery query, SourceLineCache lines, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(query.FilePath) && query.Line >= 0)
        {
            var column = query.Column >= 0 ? query.Column : lines.ColumnOf(query.FilePath, query.Line, query.Name ?? string.Empty);
            return new SourceLocation { FilePath = query.FilePath, Line = query.Line, Column = Math.Max(0, column) };
        }

        if (string.IsNullOrWhiteSpace(query.Name))
        {
            return null;
        }

        var graphs = await _graphs.GetGraphsAsync(cancellationToken).ConfigureAwait(false);
        var declarations = SymbolGraphFallback.Resolve(graphs, query);
        if (declarations.Count == 0)
        {
            return null;
        }

        var declaration = declarations[0];

        var path = declaration.FullPath;
        var line = declaration.Definition.StartLine - 1;
        return new SourceLocation { FilePath = path, Line = line, Column = Math.Max(0, lines.ColumnOf(path, line, query.Name)) };
    }

    private async Task<WorkspaceQueryResult> FinishRoslynAsync(
        string name,
        WorkspaceSymbolQuery query,
        List<WorkspaceSite> sites,
        bool truncated,
        bool callsOnly,
        int max,
        SourceLineCache lines,
        long started,
        CancellationToken cancellationToken)
    {
        var count = sites.Count;
        if (truncated)
        {
            // The budget cut Roslyn short: complete the answer from the index's edges, marked approximate.
            var graphs = await _graphs.GetGraphsAsync(cancellationToken).ConfigureAwait(false);
            var known = new HashSet<string>(sites.Select(s => Key(s.FilePath, s.Line)), StringComparer.OrdinalIgnoreCase);
            sites.AddRange(SymbolGraphFallback.Sites(SymbolGraphFallback.Resolve(graphs, query), callsOnly, lines).Where(s => known.Add(Key(s.FilePath, s.Line))));
        }

        var ordered = SymbolGraphFallback.Order(sites);
        var capped = ordered.Count > max;
        var result = new WorkspaceQueryResult(true, capped ? ordered.Take(max).ToList() : ordered, truncated || capped, WorkspaceQuerySource.Roslyn, null, Stopwatch.GetElapsedTime(started));
        var approximate = result.Sites.Count - Math.Min(count, result.Sites.Count);
        LogAnswered(_logger, name, "roslyn", count, approximate, result.Truncated, (long)result.Elapsed.TotalMilliseconds);
        return result;
    }

    private async Task<WorkspaceQueryResult> FromIndexAsync(
        string name,
        WorkspaceSymbolQuery query,
        bool callsOnly,
        int max,
        string? reason,
        SourceLineCache lines,
        long started,
        CancellationToken cancellationToken)
    {
        var graphs = string.IsNullOrWhiteSpace(query.Name) ? [] : await _graphs.GetGraphsAsync(cancellationToken).ConfigureAwait(false);
        var matches = graphs.Count == 0 ? [] : SymbolGraphFallback.Resolve(graphs, query);
        var sites = SymbolGraphFallback.Sites(matches, callsOnly, lines);
        var capped = sites.Count > max;
        var result = new WorkspaceQueryResult(
            matches.Count > 0,
            capped ? sites.Take(max).ToList() : sites,
            capped,
            matches.Count > 0 ? WorkspaceQuerySource.Index : WorkspaceQuerySource.None,
            reason + (graphs.Count == 0 ? "; no index is available" : string.Empty),
            Stopwatch.GetElapsedTime(started));
        LogFallback(_logger, name, reason, matches.Count, result.Sites.Count, (long)result.Elapsed.TotalMilliseconds);
        return result;
    }

    private static string Key(string path, int line) => path + "|" + line.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [LoggerMessage(EventId = 1400, Level = LogLevel.Debug, Message = "Workspace query {Query} answered by {Source}: {Exact} exact and {Approximate} approximate sites, truncated {Truncated}, in {Milliseconds} ms.")]
    private static partial void LogAnswered(ILogger logger, string query, string source, int exact, int approximate, bool truncated, long milliseconds);

    [LoggerMessage(EventId = 1401, Level = LogLevel.Information, Message = "Workspace query {Query} answered from the index ({Reason}): {Definitions} definitions, {Sites} approximate sites, in {Milliseconds} ms.")]
    private static partial void LogFallback(ILogger logger, string query, string? reason, int definitions, int sites, long milliseconds);
}

using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core.WorkspaceQueries;

/// <summary>Settings for <see cref="WorkspaceQueryService"/>; the defaults are design section 13's budgets.</summary>
public sealed class WorkspaceQueryOptions
{
    /// <summary>Gets or sets how long one query may run before it is stopped and answers with what it found. Default 2 s.</summary>
    public TimeSpan Budget { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets or sets how many queries may start in one second (also the largest burst). Default 5.</summary>
    public int MaxPerSecond { get; set; } = 5;

    /// <summary>Gets or sets the monotonic clock of the rate limit (tests replace it).</summary>
    public Func<TimeSpan> Clock { get; set; } = StopwatchClock;

    /// <summary>
    /// Gets or sets a test that tells whether the current thread is Visual Studio's UI thread (the VSIX passes
    /// <c>ThreadHelper.CheckAccess</c>). The query path checks it and counts any visit in
    /// <see cref="WorkspaceQueryStatistics.UiThreadVisits"/>; <see langword="null"/> skips the check.
    /// </summary>
    public Func<bool>? IsUiThread { get; set; }

    private static TimeSpan StopwatchClock() => TimeSpan.FromTicks((long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency)));
}

/// <summary>
/// The VSIX's <see cref="IWorkspaceQueryService"/> (work item 2.14): <see cref="FindReferencesAsync"/> and
/// <see cref="CallersOfAsync"/> answered by an <see cref="IWorkspaceQueryEngine"/> (the live Roslyn workspace) within the
/// budgets of design section 13, which the contract documents: one query in flight, 2 s per query, at most five a second.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A call while <c>features.workspaceQueries</c> is off fails with <see cref="PillionErrorCode.PolicyDenied"/>; a
/// call while another query runs, or over the rate, fails at once with <see cref="PillionErrorCode.Cancelled"/> and a
/// retry hint. Nothing queues. The host falls back to the index's approximate edges.</item>
/// <item>The search runs on the thread pool (<see cref="Task.Run(Func{Task})"/>) and every await leaves the context
/// (<c>ConfigureAwait(false)</c>), so no part of a query runs on the UI thread or waits for it; only the admission check
/// (a semaphore and a token bucket, no I/O) runs on the caller's thread, which for RPC is a pool thread.</item>
/// <item>At 2 s the call answers with what the engine found so far; <see cref="CallersOfResponse.Truncated"/> says so.
/// <see cref="FindReferencesResponse"/> has no such flag in contract 1.3, so a stopped find-references answers with its
/// partial list; the host knows the budget and treats an answer that took it as possibly partial.</item>
/// <item>The in-flight slot is released when the search itself ends, not when the call answers, so a search that is slow
/// to notice its cancellation never runs beside a second one.</item>
/// <item>Every query is timed with <see cref="UiThreadTimings"/> (<c>workspace.query.find_references</c>,
/// <c>workspace.query.callers_of</c>) with the thread it started on, so the gate can show that none of them ran on the
/// UI thread.</item>
/// </list>
/// The other methods of the interface are not part of this work item and refuse every call as before.
/// </remarks>
public sealed class WorkspaceQueryService : IWorkspaceQueryService
{
    /// <summary>The feature flag's name in <c>effective.features</c> (<c>FeatureNames.WorkspaceQueries</c> in Pillion.Common).</summary>
    public const string Flag = "workspaceQueries";

    /// <summary>The call sites returned when the request asks for 0.</summary>
    public const int DefaultMaxCallers = 100;

    /// <summary>The most call sites one request may ask for.</summary>
    public const int MaxCallersLimit = 1000;

    /// <summary>The most references one find-references query returns (the contract has no limit field).</summary>
    public const int MaxReferences = 500;

    /// <summary>The message of the error a call gets while <c>features.workspaceQueries</c> is off.</summary>
    public const string DisabledMessage = "Workspace queries are turned off by settings or policy (feature workspaceQueries).";

    /// <summary>The message of the error a call gets while another query is in flight.</summary>
    public const string BusyMessage = "Another workspace query is in flight.";

    /// <summary>The message of the error a call gets over the rate limit.</summary>
    public const string RateMessage = "Too many workspace queries: at most five a second.";

    private readonly IWorkspaceQueryEngine _engine;
    private readonly Func<bool> _isEnabled;
    private readonly PillionLog? _log;
    private readonly WorkspaceQueryOptions _options;
    private int _inFlight; // 1 while a search runs: the one-in-flight rule, taken without waiting.
    private readonly TokenBucket _rate;
    private readonly NotImplementedWorkspaceQueryService _others = new();

    /// <summary>Initializes a new instance of the <see cref="WorkspaceQueryService"/> class.</summary>
    /// <param name="engine">The Roslyn side.</param>
    /// <param name="isEnabled">Reads <c>features.workspaceQueries</c> (the host's effective flag).</param>
    /// <param name="log">The extension log, or <see langword="null"/>.</param>
    /// <param name="options">The budgets, or <see langword="null"/> for the defaults.</param>
    public WorkspaceQueryService(IWorkspaceQueryEngine engine, Func<bool> isEnabled, PillionLog? log = null, WorkspaceQueryOptions? options = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
        _log = log;
        _options = options ?? new WorkspaceQueryOptions();
        if (_options.Budget <= TimeSpan.Zero)
        {
            throw new ArgumentException("The budget must be positive.", nameof(options));
        }

        _rate = new TokenBucket(Math.Max(1, _options.MaxPerSecond), Math.Max(1, _options.MaxPerSecond), _options.Clock);
    }

    /// <summary>Gets the counters.</summary>
    public WorkspaceQueryStatistics Statistics { get; } = new();

    /// <inheritdoc/>
    public Task<FindReferencesResponse> FindReferencesAsync(FindReferencesRequest request, CancellationToken cancellationToken)
    {
        if (request?.Location is not { } location || string.IsNullOrWhiteSpace(location.FilePath) || location.Line < 0)
        {
            return Task.FromResult(new FindReferencesResponse());
        }

        if (TryAdmit(out var refusal) is false)
        {
            return Task.FromException<FindReferencesResponse>(refusal!);
        }

        var query = new SourceLocation { FilePath = location.FilePath, Line = location.Line, Column = Math.Max(0, location.Column) };
        var results = new WorkspaceQueryResults<SourceLocation>(MaxReferences, ReferenceKey);
        return RunAsync(
            "find_references",
            results,
            token => _engine.FindReferencesAsync(query, results, token),
            _ => new FindReferencesResponse
            {
                References = results.ToArray()
                    .OrderBy(r => r.FilePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(r => r.Line)
                    .ThenBy(r => r.Column)
                    .ToArray(),
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<CallersOfResponse> CallersOfAsync(CallersOfRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SymbolName))
        {
            return Task.FromResult(new CallersOfResponse());
        }

        if (TryAdmit(out var refusal) is false)
        {
            return Task.FromException<CallersOfResponse>(refusal!);
        }

        var max = request.MaxResults <= 0 ? DefaultMaxCallers : Math.Min(request.MaxResults, MaxCallersLimit);
        var query = new CallersOfRequest
        {
            SymbolName = request.SymbolName.Trim(),
            Container = request.Container?.Trim() ?? string.Empty,
            FilePath = request.FilePath ?? string.Empty,
            Line = request.Line,
            MaxResults = max,
        };
        var results = new WorkspaceQueryResults<CallSite>(max, CallSiteKey);
        return RunAsync(
            "callers_of",
            results,
            token => _engine.CallersOfAsync(query, results, token),
            timedOut => new CallersOfResponse
            {
                IsFound = results.IsFound,
                Callers = results.ToArray()
                    .OrderBy(c => c.FilePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(c => c.Line)
                    .ToArray(),
                Truncated = timedOut || results.Overflowed,
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<DiagnosticsResponse> GetDiagnosticsAsync(DiagnosticsRequest request, CancellationToken cancellationToken) =>
        _others.GetDiagnosticsAsync(request, cancellationToken);

    /// <inheritdoc/>
    public Task<SymbolResponse> GetSymbolAsync(SymbolRequest request, CancellationToken cancellationToken) =>
        _others.GetSymbolAsync(request, cancellationToken);

    /// <inheritdoc/>
    public Task<RenameSymbolResponse> RenameSymbolAsync(RenameSymbolRequest request, CancellationToken cancellationToken) =>
        _others.RenameSymbolAsync(request, cancellationToken);

    /// <inheritdoc/>
    public Task<RunTestsResponse> RunTestsAsync(RunTestsRequest request, CancellationToken cancellationToken) =>
        _others.RunTestsAsync(request, cancellationToken);

    private static string ReferenceKey(SourceLocation location) =>
        location.FilePath + "|" + location.Line.ToString(CultureInfo.InvariantCulture) + "|" + location.Column.ToString(CultureInfo.InvariantCulture);

    // Two calls on one line are one call site for the reader (the snippet is the line).
    private static string CallSiteKey(CallSite site) => site.FilePath + "|" + site.Line.ToString(CultureInfo.InvariantCulture);

    private static PillionRpcException Refusal(PillionErrorCode code, string message, string retryHint) =>
        new(new PillionError { Code = code, Message = message, RetryHint = retryHint });

    /// <summary>Checks the flag, takes the in-flight slot and a rate token, or says why not. Never blocks.</summary>
    private bool TryAdmit(out PillionRpcException? refusal)
    {
        if (!_isEnabled())
        {
            Statistics.OnRefusedDisabled();
            refusal = Refusal(PillionErrorCode.PolicyDenied, DisabledMessage, "Turn features.workspaceQueries on to use the Roslyn workspace; the index's approximate edges still answer.");
            return false;
        }

        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            Statistics.OnRefusedBusy();
            refusal = Refusal(PillionErrorCode.Cancelled, BusyMessage, "Retry when the current query ends (within 2 s), or answer from the index's approximate edges.");
            return false;
        }

        if (!_rate.TryTake(out var retryAfter))
        {
            Volatile.Write(ref _inFlight, 0);
            Statistics.OnRefusedRate();
            refusal = Refusal(
                PillionErrorCode.Cancelled,
                RateMessage,
                string.Format(CultureInfo.InvariantCulture, "Retry in {0:0} ms, or answer from the index's approximate edges.", Math.Ceiling(retryAfter.TotalMilliseconds)));
            return false;
        }

        refusal = null;
        return true;
    }

    /// <summary>Runs an admitted query on the thread pool within the budget; releases the slot when the search ends.</summary>
    private async Task<TResponse> RunAsync<T, TResponse>(
        string name,
        WorkspaceQueryResults<T> results,
        Func<CancellationToken, Task> search,
        Func<bool, TResponse> respond,
        CancellationToken cancellationToken)
        where T : class
    {
        var started = Stopwatch.GetTimestamp();
        var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task work;
        try
        {
            budget.CancelAfter(_options.Budget);
            work = Task.Run(
                async () =>
                {
                    using var timing = UiThreadTimings.Measure("workspace.query." + name);
                    CheckThread(name);
                    await search(budget.Token).ConfigureAwait(false);
                    CheckThread(name);
                },
                CancellationToken.None);
        }
        catch
        {
            budget.Dispose();
            timer.Dispose();
            Volatile.Write(ref _inFlight, 0);
            throw;
        }

        // The slot belongs to the search, not to this call: it is released when the search has really ended.
        _ = work.ContinueWith(
            t =>
            {
                _ = t.Exception; // Observed here; the call below reports it when it is still waiting.
                budget.Dispose();
                Volatile.Write(ref _inFlight, 0);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            var deadline = Task.Delay(_options.Budget, timer.Token);
            var finished = await Task.WhenAny(work, deadline).ConfigureAwait(false);
            timer.Cancel();
            cancellationToken.ThrowIfCancellationRequested();

            var timedOut = finished != work;
            if (!timedOut)
            {
                try
                {
                    await work.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    timedOut = true; // The budget cancelled the search just before the delay fired.
                }
            }

            CheckThread(name);
            var elapsed = Elapsed(started);
            Statistics.OnCompleted(elapsed, timedOut);
            var response = respond(timedOut);
            _log?.Info(
                "workspace.query",
                "Workspace query answered.",
                ("query", name),
                ("ms", (long)elapsed.TotalMilliseconds),
                ("found", results.IsFound),
                ("results", results.Count),
                ("timedOut", timedOut),
                ("overflowed", results.Overflowed));
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PillionRpcException ex)
        {
            Statistics.OnFailed();
            _log?.Info("workspace.query.refused", "Workspace query refused by the engine.", ("query", name), ("code", ex.Error.Code), ("detail", ex.Error.Details));
            throw;
        }
        catch (Exception ex)
        {
            Statistics.OnFailed();
            _log?.Warning("workspace.query.failed", "Workspace query failed.", ("query", name), ("error", ex.GetType().Name), ("detail", ex.Message));
            throw;
        }
        finally
        {
            timer.Dispose();
        }
    }

    private static TimeSpan Elapsed(long started) =>
        TimeSpan.FromTicks((long)((Stopwatch.GetTimestamp() - started) * (10_000_000.0 / Stopwatch.Frequency)));

    private void CheckThread(string name)
    {
        if (_options.IsUiThread?.Invoke() == true)
        {
            Statistics.OnUiThreadVisit();
            _log?.Warning("workspace.query.uithread", "A workspace query ran on the UI thread.", ("query", name));
        }
    }
}

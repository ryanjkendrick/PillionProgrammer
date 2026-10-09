using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Pillion.VisualStudio.Core.Signals;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>One signal seen by <see cref="SignalRecording"/>.</summary>
/// <param name="Kind">The event's name (<c>BuildStarted</c>, <c>DocumentOpened</c>, ...).</param>
/// <param name="Path">The document path for document events, otherwise <see langword="null"/>.</param>
/// <param name="Build">The build result for <c>BuildFinished</c>, otherwise <see langword="null"/>.</param>
/// <param name="Sequence">The order the signal was seen in.</param>
public sealed record SeenSignal(string Kind, string? Path, BuildFinishedEventArgs? Build, int Sequence);

/// <summary>
/// Records the events of the extension's <see cref="IIdeSignals"/> service for a test. Kept out of the test classes so test
/// discovery, which runs outside Visual Studio, never needs Pillion.VisualStudio.Core; use it only after the package has
/// loaded (<see cref="PillionIdeTest.EnsurePackageLoadedAsync"/>).
/// </summary>
public sealed class SignalRecording : IDisposable
{
    private readonly object _gate = new();
    private readonly List<SeenSignal> _seen = [];
    private readonly IIdeSignals _signals;

    private SignalRecording(IIdeSignals signals)
    {
        _signals = signals;
        signals.BuildStarted += OnBuildStarted;
        signals.BuildFinished += OnBuildFinished;
        signals.DocumentOpened += OnDocumentOpened;
        signals.DocumentSaved += OnDocumentSaved;
        signals.DocumentClosed += OnDocumentClosed;
        signals.ActiveDocumentChanged += OnActiveDocumentChanged;
        signals.DiagnosticsChanged += OnDiagnosticsChanged;
        signals.BuildStateChanged += OnBuildStateChanged;
    }

    /// <summary>Gets the service itself.</summary>
    public IIdeSignals Signals => _signals;

    /// <summary>Gets everything seen so far, oldest first.</summary>
    public IReadOnlyList<SeenSignal> Seen
    {
        get
        {
            lock (_gate)
            {
                return [.. _seen];
            }
        }
    }

    /// <summary>
    /// Gets the <see cref="IIdeSignals"/> service, waits until the package has connected it to the Error List, and starts
    /// recording.
    /// </summary>
    /// <param name="timeout">How long to wait for the connection.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The recording.</returns>
    public static async Task<SignalRecording> StartAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var service = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(IIdeSignals)).ConfigureAwait(false);
        if (service is null)
        {
            throw new InvalidOperationException("The IIdeSignals service is not registered; is the Pillion package installed in this Experimental Instance?");
        }

        if (service is not IIdeSignals signals)
        {
            throw new InvalidOperationException(
                $"The IIdeSignals service is a {service.GetType().AssemblyQualifiedName} from {service.GetType().Assembly.Location}, "
                + $"which does not implement the test's IIdeSignals from {typeof(IIdeSignals).Assembly.Location}.");
        }

        // StartAsync runs after the package's initialization; the Error List is the last thing it connects.
        var clock = Stopwatch.StartNew();
        while (signals.DiagnosticsNote.StartsWith("The Error List is not available yet.", StringComparison.Ordinal))
        {
            if (clock.Elapsed > timeout)
            {
                throw new TimeoutException($"The IDE signals were not connected to the Error List after {timeout.TotalSeconds:0} s. Note: {signals.DiagnosticsNote}");
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return new SignalRecording(signals);
    }

    /// <summary>Waits until a signal that matches has been seen.</summary>
    /// <param name="kind">The event name.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <param name="match">An extra condition on the signal, or <see langword="null"/>.</param>
    /// <returns>The first matching signal.</returns>
    public async Task<SeenSignal> WaitForAsync(string kind, TimeSpan timeout, CancellationToken cancellationToken, Func<SeenSignal, bool>? match = null)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var found = Seen.FirstOrDefault(s => s.Kind == kind && (match is null || match(s)));
            if (found is not null)
            {
                return found;
            }

            if (clock.Elapsed > timeout)
            {
                throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0} s waiting for {kind}. Seen: {string.Join(", ", Seen.Select(s => s.Kind + (s.Path is null ? string.Empty : " " + s.Path)))}");
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Polls the Solution-scope diagnostics until one has the code.</summary>
    /// <param name="code">The diagnostic code, for example <c>CS0029</c>.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>All diagnostics at the time the code appeared.</returns>
    public async Task<IReadOnlyList<DiagnosticInfo>> WaitForSolutionDiagnosticAsync(string code, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var all = await _signals.GetDiagnosticsAsync(DiagnosticScope.Solution, cancellationToken).ConfigureAwait(false);
            if (all.Any(d => d.Code == code))
            {
                return all;
            }

            if (clock.Elapsed > timeout)
            {
                throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0} s waiting for {code} in the Error List. Found {all.Count} entries. {_signals.DiagnosticsNote}");
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Polls an Output pane until its tail contains the text.</summary>
    /// <param name="pane">The pane name.</param>
    /// <param name="text">The text to look for (case-insensitive).</param>
    /// <param name="maxChars">The tail length to ask for.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The pane text when it contains <paramref name="text"/>.</returns>
    public async Task<string> WaitForOutputAsync(string pane, string text, int maxChars, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var output = await _signals.GetOutputPaneTextAsync(pane, maxChars, cancellationToken).ConfigureAwait(false);
            if (output is not null && output.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return output;
            }

            if (clock.Elapsed > timeout)
            {
                throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0} s waiting for \"{text}\" in the {pane} pane. Pane text: {(output is null ? "(no such pane)" : output)}");
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _signals.BuildStarted -= OnBuildStarted;
        _signals.BuildFinished -= OnBuildFinished;
        _signals.DocumentOpened -= OnDocumentOpened;
        _signals.DocumentSaved -= OnDocumentSaved;
        _signals.DocumentClosed -= OnDocumentClosed;
        _signals.ActiveDocumentChanged -= OnActiveDocumentChanged;
        _signals.DiagnosticsChanged -= OnDiagnosticsChanged;
        _signals.BuildStateChanged -= OnBuildStateChanged;
    }

    private void Add(string kind, string? path = null, BuildFinishedEventArgs? build = null)
    {
        lock (_gate)
        {
            _seen.Add(new SeenSignal(kind, path, build, _seen.Count));
        }
    }

    private void OnBuildStarted(object? sender, EventArgs e) => Add("BuildStarted");

    private void OnBuildFinished(object? sender, BuildFinishedEventArgs e) => Add("BuildFinished", build: e);

    private void OnDocumentOpened(object? sender, DocumentEventArgs e) => Add("DocumentOpened", e.Path);

    private void OnDocumentSaved(object? sender, DocumentEventArgs e) => Add("DocumentSaved", e.Path);

    private void OnDocumentClosed(object? sender, DocumentEventArgs e) => Add("DocumentClosed", e.Path);

    private void OnActiveDocumentChanged(object? sender, DocumentEventArgs e) => Add("ActiveDocumentChanged", e.Path);

    private void OnDiagnosticsChanged(object? sender, EventArgs e) => Add("DiagnosticsChanged");

    private void OnBuildStateChanged(object? sender, BuildStateChangedEventArgs e) => Add(e.IsBuilding ? "BuildStateChanged(building)" : "BuildStateChanged(idle)");
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Suggestions;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Autocomplete;
using Pillion.VisualStudio.Core.InlineChat;
using Pillion.VisualStudio.Core.Mentions;
using Task = System.Threading.Tasks.Task;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>How a typing pass of <see cref="GateScenarioChecks.TypeSnippetAsync"/> runs.</summary>
/// <param name="CharacterDelay">The time between two typed characters.</param>
/// <param name="LinePause">The pause after each line break, while a suggestion may appear.</param>
/// <param name="AcceptMatchingProposals">Whether a displayed Pillion proposal that equals the next lines of the snippet is accepted.</param>
public sealed record TypingOptions(TimeSpan CharacterDelay, TimeSpan LinePause, bool AcceptMatchingProposals);

/// <summary>
/// The Gate 1 performance scenarios that use Pillion.VisualStudio.Core and Pillion.Contracts types (work item 1.18; Gate 1
/// rows "Autocomplete latency", "Keystroke latency", "Prefix cache hit rate", "Chat first token", "Inline chat preview,
/// first line" and "Mentions and inspection"). Kept out of the test class, like <see cref="PillionHostChecks"/>, so test
/// discovery outside Visual Studio never needs those assemblies; call these only after the package has loaded. They use the
/// real Pillion Host and a real local model: nothing here sets <see cref="AutocompleteOverrides"/> or
/// <see cref="InlineChatOverrides"/>.
/// </summary>
public static class GateScenarioChecks
{
    /// <summary>The mention kinds pinned from the picker after the file.</summary>
    private static readonly string[] MentionKinds = ["errors", "selection"];

    /// <summary>Stands for Enter in <see cref="DismissCompletionBefore"/>.</summary>
    private const char LineBreak = '\n';

    /// <summary>The 40-line C# file the typing scenario types (indentation is left to the editor's smart indent).</summary>
    public static readonly IReadOnlyList<string> Snippet =
    [
        "using System;",
        "using System.Collections.Generic;",
        "using System.Linq;",
        string.Empty,
        "namespace Scratch",
        "{",
        "    public sealed class InventoryService",
        "    {",
        "        private readonly Dictionary<string, int> _stock = new Dictionary<string, int>();",
        string.Empty,
        "        public int Count => _stock.Count;",
        string.Empty,
        "        public void Add(string sku, int quantity)",
        "        {",
        "            if (string.IsNullOrWhiteSpace(sku))",
        "            {",
        "                throw new ArgumentException(\"A SKU is required.\", nameof(sku));",
        "            }",
        string.Empty,
        "            if (quantity <= 0)",
        "            {",
        "                throw new ArgumentOutOfRangeException(nameof(quantity));",
        "            }",
        string.Empty,
        "            _stock.TryGetValue(sku, out var current);",
        "            _stock[sku] = current + quantity;",
        "        }",
        string.Empty,
        "        public bool TryRemove(string sku, int quantity)",
        "        {",
        "            if (!_stock.TryGetValue(sku, out var current) || current < quantity)",
        "            {",
        "                return false;",
        "            }",
        string.Empty,
        "            _stock[sku] = current - quantity;",
        "            return true;",
        "        }",
        "    }",
        "}",
    ];

    /// <summary>The settings file Pillion Host reads (and watches).</summary>
    public static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PillionProgrammer", "settings.json");

    /// <summary>Gets the folder the scenario results are written to: <c>%PILLION_GATE_OUT%</c>, or the test log folder.</summary>
    /// <param name="fallback">The folder to use when the variable is not set.</param>
    /// <returns>The folder, created.</returns>
    public static string OutputDirectory(string fallback)
    {
        var configured = Environment.GetEnvironmentVariable("PILLION_GATE_OUT");
        var folder = string.IsNullOrEmpty(configured) ? fallback : configured!;
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Gets a setting of the run from the environment, or a default.</summary>
    /// <param name="name">The variable.</param>
    /// <param name="fallback">The default.</param>
    /// <returns>The value.</returns>
    public static string Setting(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? fallback : value!;
    }

    /// <summary>Writes a result object as indented JSON.</summary>
    /// <param name="folder">The folder.</param>
    /// <param name="name">The file name without extension.</param>
    /// <param name="result">The result.</param>
    /// <returns>The path.</returns>
    public static string WriteResult(string folder, string name, JObject result)
    {
        var path = Path.Combine(folder, name + ".json");
        File.WriteAllText(path, result.ToString(Formatting.Indented));
        return path;
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Settings and the extension's autocomplete engine
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Changes settings.json in place (atomically: a temporary file, then a replace), the way the Options pages do; Pillion
    /// Host's file watcher reloads it and tells the extension. The gate script backs the file up and restores it.
    /// </summary>
    /// <param name="change">The change.</param>
    public static void UpdateSettings(Action<JObject> change)
    {
        var json = File.Exists(SettingsPath) ? JObject.Parse(File.ReadAllText(SettingsPath)) : new JObject();
        change(json);
        var temporary = SettingsPath + ".gate1.tmp";
        File.WriteAllText(temporary, json.ToString(Formatting.Indented));
        if (File.Exists(SettingsPath))
        {
            File.Replace(temporary, SettingsPath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temporary, SettingsPath);
        }
    }

    /// <summary>Sets a property under an object path, creating the objects on the way.</summary>
    /// <param name="root">The settings.</param>
    /// <param name="path">The object path, for example <c>routing.profiles</c>.</param>
    /// <param name="name">The property.</param>
    /// <param name="value">The value.</param>
    public static void Set(JObject root, string path, string name, JToken value)
    {
        var node = root;
        foreach (var part in path.Split('.'))
        {
            if (node[part] is not JObject child)
            {
                child = new JObject();
                node[part] = child;
            }

            node = child;
        }

        node[name] = value;
    }

    /// <summary>Gets the extension's autocomplete feature object (internal to the package assembly; read by reflection).</summary>
    /// <param name="timeout">How long to wait for the package to create it.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The feature.</returns>
    public static async Task<object> GetAutocompleteFeatureAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => string.Equals(a.GetName().Name, "Pillion.VisualStudio", StringComparison.Ordinal));
            var packageType = assembly?.GetType("Pillion.VisualStudio.PillionPackage");
            var package = packageType?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
            var feature = package is null ? null : packageType!.GetField("_autocomplete", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(package);
            if (feature is not null)
            {
                return feature;
            }

            if (clock.Elapsed > timeout)
            {
                throw new TimeoutException("The package's autocomplete feature was not created (PillionPackage.Instance._autocomplete).");
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Gets the autocomplete engine of the feature.</summary>
    /// <param name="feature">The feature (<see cref="GetAutocompleteFeatureAsync"/>).</param>
    /// <returns>The engine.</returns>
    public static AutocompleteEngine Engine(object feature) => (AutocompleteEngine)feature.GetType().GetProperty("Engine")!.GetValue(feature)!;

    /// <summary>Gets the feature flag and settings follower of the feature.</summary>
    /// <param name="feature">The feature.</param>
    /// <returns>The availability.</returns>
    public static AutocompleteAvailability Availability(object feature) => (AutocompleteAvailability)feature.GetType().GetProperty("Availability")!.GetValue(feature)!;

    /// <summary>
    /// Routes autocomplete to one model (Local-fast set to it) or switches autocomplete on or off in settings.json, then
    /// waits until the host has announced the change and the extension has read it.
    /// </summary>
    /// <param name="feature">The feature.</param>
    /// <param name="providerId">The Ollama provider id.</param>
    /// <param name="model">The model for Local-fast, or <see langword="null"/> to leave the routing as it is.</param>
    /// <param name="enabled">Whether autocomplete is on.</param>
    /// <param name="cancellationToken">Cancels the waits.</param>
    /// <param name="profile">The profile autocomplete is routed to: <c>localFast</c>, or <c>fast</c> for a cloud model (which also turns on the cloud switch for autocomplete).</param>
    /// <returns>What was changed and how long it took to take effect.</returns>
    public static async Task<string> ConfigureAutocompleteAsync(object feature, string providerId, string? model, bool enabled, CancellationToken cancellationToken, string profile = "localFast")
    {
        var connection = await ConnectionAsync().ConfigureAwait(false);
        var changed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, HostSettingsChangedEventArgs e) => changed.TrySetResult(string.Join(",", e.Sections));
        connection.SettingsChanged += OnChanged;
        var clock = Stopwatch.StartNew();
        try
        {
            UpdateSettings(json =>
            {
                if (model is not null)
                {
                    Set(json, "routing.profiles", profile, providerId + "/" + model);
                    Set(json, "routing.tasks", "autocomplete", profile);
                    if (profile != "localFast")
                    {
                        Set(json, "cloudSwitches", "autocomplete", true);
                    }
                }

                Set(json, "features", "autocomplete", enabled);
            });

            var winner = await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(15), cancellationToken)).ConfigureAwait(false);
            var notified = winner == changed.Task;
            var availability = Availability(feature);
            var engine = Engine(feature);
            while (engine.Settings.Enabled != enabled)
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(30))
                {
                    throw new TimeoutException($"Autocomplete did not become {(enabled ? "enabled" : "disabled")} after settings.json changed.");
                }

                await availability.RefreshAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }

            // Give the host a moment to rebuild its routing after the notification.
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1}, autocomplete {2}; settingsChanged {3}, in effect after {4:0} ms",
                profile,
                model is null ? "unchanged" : providerId + "/" + model,
                enabled ? "on" : "off",
                notified ? "received (" + await changed.Task.ConfigureAwait(false) + ")" : "not seen within 15 s",
                clock.Elapsed.TotalMilliseconds);
        }
        finally
        {
            connection.SettingsChanged -= OnChanged;
        }
    }

    /// <summary>Gets the connection to Pillion Host.</summary>
    /// <returns>The connection.</returns>
    public static async Task<IHostConnection> ConnectionAsync() =>
        await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(IHostConnection)).ConfigureAwait(false) as IHostConnection
        ?? throw new InvalidOperationException("The IHostConnection service is not registered.");

    /// <summary>Gets the host's autocomplete counters (cumulative since the host started).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The counters.</returns>
    public static async Task<AutocompleteHostCounters> HostCountersAsync(CancellationToken cancellationToken)
    {
        var connection = await ConnectionAsync().ConfigureAwait(false);
        return await connection.GetProxy<IDiagnosticsService>().GetAutocompleteCountersAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets the host's autocomplete counters as an opaque object (for callers that must not name Contracts types).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The counters.</returns>
    public static async Task<object> CaptureHostCountersAsync(CancellationToken cancellationToken) => await HostCountersAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Forgets the extension's autocomplete figures so far, including the degradation state (a fresh measurement).</summary>
    /// <param name="feature">The feature.</param>
    public static void ResetAutocompleteMetrics(object feature) => Engine(feature).Metrics.Reset();

    /// <summary>Gets the host's Outbound records, newest last.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The records.</returns>
    public static async Task<OutboundRecordDto[]> OutboundAsync(CancellationToken cancellationToken)
    {
        var connection = await ConnectionAsync().ConfigureAwait(false);
        return await connection.GetProxy<IDiagnosticsService>().GetOutboundRecordsAsync(500, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Typing
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Switches off the view's brace completion, so a typed closing brace or quote is not doubled; smart indent and
    /// formatting stay on. (Completion lists stay on as well; the typing pass dismisses an open list before a character
    /// that would commit it, so the typed text stays the snippet's.)
    /// </summary>
    /// <param name="view">The view.</param>
    public static void PrepareEditor(IWpfTextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        view.Options.SetOptionValue(DefaultTextViewOptions.BraceCompletionEnabledOptionId, false);
    }

    /// <summary>
    /// Dismisses an open completion list (without a key press, so a displayed ghost-text proposal stays) when the next
    /// key would commit its selected item: Enter or a character that is not part of an identifier.
    /// </summary>
    private static bool DismissCompletionBefore(IWpfTextView view, IAsyncCompletionBroker broker, char next)
    {
        if (next != LineBreak && (char.IsLetterOrDigit(next) || next == '_'))
        {
            return false;
        }

        var session = broker.GetSession(view);
        if (session is null || session.IsDismissed)
        {
            return false;
        }

        session.Dismiss();
        return true;
    }

    /// <summary>
    /// Types the snippet into the view through its command chain, one character at a time at the given cadence, pausing
    /// after every line break while a suggestion may appear, and measures each keystroke: the time the TypeChar command
    /// took on the UI thread (the character is in the buffer when it returns) and how late the UI thread let the next
    /// keystroke start. During a pause, a Pillion proposal that equals the next lines is accepted when the options say so.
    /// Runs on the UI thread and yields it between keystrokes.
    /// </summary>
    /// <param name="view">The view (an empty document with keyboard focus).</param>
    /// <param name="options">The cadence.</param>
    /// <param name="acceptAsync">Accepts the displayed proposal (Tab or Edit.AcceptSuggestion).</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>The measurements.</returns>
    public static async Task<JObject> TypeSnippetAsync(IWpfTextView view, TypingOptions options, Func<Task> acceptAsync, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var typeChar = new List<double>();
        var returns = new List<double>();
        var lateness = new List<double>();
        var visible = new List<double>();
        var notApplied = 0;
        var accepted = 0;
        var acceptedLines = 0;
        var pausesWithPillion = 0;
        var pausesWithOther = 0;
        var pauses = 0;
        var mismatches = new List<string>();
        var total = Stopwatch.StartNew();
        var target = CommandTarget(view);
        var broker = ((IComponentModel)Package.GetGlobalService(typeof(SComponentModel))).GetService<IAsyncCompletionBroker>();
        var dismissed = 0;
        var variant = Marshal.AllocCoTaskMem(32);
        try
        {
            var line = 0;
            while (line < Snippet.Count)
            {
                // A pause at the start of the line: the moment a developer looks for a suggestion.
                var (seenAfter, other) = await PauseAsync(view, options.LinePause, cancellationToken).ConfigureAwait(true);
                pauses++;
                if (seenAfter is { } ms)
                {
                    visible.Add(ms);
                    pausesWithPillion++;
                }
                else if (other)
                {
                    pausesWithOther++;
                }

                var take = 0;
                if (options.AcceptMatchingProposals && seenAfter is not null && AutocompleteChecks.IsPillionDisplayed(view))
                {
                    take = MatchingLines(DisplayedText(view), line);
                    if (take > 0)
                    {
                        await acceptAsync().ConfigureAwait(true);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                        accepted++;
                        acceptedLines += take;
                    }
                    else if (mismatches.Count < 5)
                    {
                        mismatches.Add(DisplayedText(view).Split('\n')[0].Trim());
                    }
                }

                if (take == 0)
                {
                    // Keystrokes follow a fixed schedule (one every CharacterDelay from the start of the line), as a typist's
                    // fingers do: a keystroke the UI thread lets start late does not push the later ones back.
                    var due = Ms(Stopwatch.GetTimestamp());
                    foreach (var c in Snippet[line].Trim())
                    {
                        due += options.CharacterDelay.TotalMilliseconds;
                        var wait = due - Ms(Stopwatch.GetTimestamp());
                        if (wait > 0)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(wait), cancellationToken).ConfigureAwait(true);
                        }

                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                        lateness.Add(Math.Max(0, Ms(Stopwatch.GetTimestamp()) - due));

                        if (DismissCompletionBefore(view, broker, c))
                        {
                            dismissed++;
                        }

                        Marshal.GetNativeVariantForObject((ushort)c, variant);
                        var version = view.TextBuffer.CurrentSnapshot.Version.VersionNumber;
                        var started = Stopwatch.GetTimestamp();
                        Exec(target, VSConstants.VSStd2KCmdID.TYPECHAR, variant);
                        typeChar.Add(Ms(Stopwatch.GetTimestamp() - started));
                        if (view.TextBuffer.CurrentSnapshot.Version.VersionNumber == version)
                        {
                            notApplied++;
                        }
                    }

                    take = 1;
                }

                line += take;
                if (line >= Snippet.Count)
                {
                    break;
                }

                await Task.Delay(options.CharacterDelay, cancellationToken).ConfigureAwait(true);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                if (DismissCompletionBefore(view, broker, LineBreak))
                {
                    dismissed++;
                }

                var enter = Stopwatch.GetTimestamp();
                Exec(target, VSConstants.VSStd2KCmdID.RETURN, IntPtr.Zero);
                returns.Add(Ms(Stopwatch.GetTimestamp() - enter));
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(variant);
        }

        total.Stop();
        var typed = view.TextBuffer.CurrentSnapshot.GetText();
        var expected = string.Join("\n", Snippet.Select(l => l.Trim()));
        var actual = string.Join("\n", typed.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()));
        return new JObject
        {
            ["characterDelayMs"] = options.CharacterDelay.TotalMilliseconds,
            ["linePauseMs"] = options.LinePause.TotalMilliseconds,
            ["durationMs"] = Math.Round(total.Elapsed.TotalMilliseconds),
            ["lines"] = Snippet.Count,
            ["typedCharacters"] = typeChar.Count,
            ["typeChar"] = Percentiles(typeChar),
            ["return"] = Percentiles(returns),
            ["uiLateness"] = Percentiles(lateness),
            ["charactersNotInBufferAfterTypeChar"] = notApplied,
            ["completionListsDismissed"] = dismissed,
            ["pauses"] = pauses,
            ["pausesWithPillionProposal"] = pausesWithPillion,
            ["pausesWithAnotherProvidersProposal"] = pausesWithOther,
            ["lineBreakToVisibleProposal"] = Percentiles(visible),
            ["acceptedProposals"] = accepted,
            ["acceptedLines"] = acceptedLines,
            ["firstNonMatchingProposals"] = new JArray(mismatches),
            ["finalTextMatchesSnippetIgnoringIndentation"] = string.Equals(expected, actual, StringComparison.Ordinal),
        };
    }

    /// <summary>Waits for a Pillion proposal for up to <paramref name="pause"/>, polling every 10 ms.</summary>
    private static async Task<(double? SeenAfterMs, bool Other)> PauseAsync(IWpfTextView view, TimeSpan pause, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        double? seen = null;
        var other = false;
        while (clock.Elapsed < pause)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (seen is null)
            {
                var id = AutocompleteChecks.DisplayedProposalId(view);
                if (PillionProposalId.IsPillion(id))
                {
                    seen = clock.Elapsed.TotalMilliseconds;
                }
                else if (id is not null)
                {
                    other = true;
                }
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(true);
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        return (seen, other);
    }

    /// <summary>Gets the text the displayed proposal would insert (the concatenated edits).</summary>
    private static string DisplayedText(IWpfTextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var componentModel = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
        var proposal = componentModel.DefaultExportProvider.GetExportedValueOrDefault<SuggestionServiceBase>()?.GetDisplayedProposal(view);
        return proposal?.Edits is { } edits ? string.Concat(edits.Select(e => e.ReplacementText)).Replace("\r\n", "\n") : string.Empty;
    }

    /// <summary>How many snippet lines from <paramref name="line"/> the proposal reproduces exactly (ignoring indentation), or 0.</summary>
    private static int MatchingLines(string proposal, int line)
    {
        var lines = proposal.Split('\n').Select(l => l.Trim()).ToList();
        while (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count == 0 || line + lines.Count > Snippet.Count)
        {
            return 0;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            if (!string.Equals(lines[i], Snippet[line + i].Trim(), StringComparison.Ordinal))
            {
                return 0;
            }
        }

        return lines.Count;
    }

    private static Microsoft.VisualStudio.OLE.Interop.IOleCommandTarget CommandTarget(IWpfTextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var componentModel = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
        var adapters = componentModel.GetService<Microsoft.VisualStudio.Editor.IVsEditorAdaptersFactoryService>();
        return (Microsoft.VisualStudio.OLE.Interop.IOleCommandTarget)adapters.GetViewAdapter(view)!;
    }

    private static void Exec(Microsoft.VisualStudio.OLE.Interop.IOleCommandTarget target, VSConstants.VSStd2KCmdID command, IntPtr variant)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var group = VSConstants.VSStd2K;
        _ = target.Exec(ref group, (uint)command, 0, variant, IntPtr.Zero);
    }

    /// <summary>Describes the extension's autocomplete figures after a pass, with the host's counters as deltas over the pass.</summary>
    /// <param name="feature">The feature.</param>
    /// <param name="hostBeforePass">The host's counters before the pass (<see cref="CaptureHostCountersAsync"/>).</param>
    /// <param name="passStartedUtc">When the pass started.</param>
    /// <param name="expectedModel">The model autocomplete was routed to.</param>
    /// <param name="cancellationToken">Cancels the host calls.</param>
    /// <returns>The figures.</returns>
    public static async Task<JObject> DescribeAutocompleteAsync(object feature, object hostBeforePass, DateTime passStartedUtc, string? expectedModel, CancellationToken cancellationToken)
    {
        var hostBefore = (AutocompleteHostCounters)hostBeforePass;
        var engine = Engine(feature);
        var snapshot = engine.Metrics.Snapshot();
        var host = await HostCountersAsync(cancellationToken).ConfigureAwait(false);
        var outbound = (await OutboundAsync(cancellationToken).ConfigureAwait(false))
            .Where(r => r.TaskClass == TaskClass.Autocomplete && r.TimestampUtc >= passStartedUtc)
            .ToList();
        var recent = snapshot.Recent;
        var hits = (host.CacheHits - hostBefore.CacheHits) + (host.TypeThroughHits - hostBefore.TypeThroughHits);
        var lookups = hits + (host.CacheMisses - hostBefore.CacheMisses);
        return new JObject
        {
            ["showStatusText"] = AutocompleteMetricsText.Describe(snapshot, host),
            ["requests"] = recent.Requests,
            ["attempts"] = recent.Attempts,
            ["shown"] = recent.Shown,
            ["accepted"] = recent.Accepted,
            ["acceptedWord"] = recent.AcceptedWord,
            ["rejected"] = recent.Rejected,
            ["displaced"] = recent.Displaced,
            ["cancelledByKeystroke"] = recent.CancelledByKeystroke,
            ["empty"] = recent.Empty,
            ["errors"] = recent.Errors,
            ["multiLine"] = recent.MultiLine,
            ["showRate"] = recent.ShowRate,
            ["acceptanceRate"] = recent.AcceptanceRate,
            ["timeToFirstChunk"] = Durations(recent.TimeToFirstChunk),
            ["timeToSuggestion"] = Durations(recent.TimeToSuggestion),
            ["degraded"] = snapshot.IsDegraded,
            ["degradation"] = snapshot.Degradation is { } d ? d.Reason : null,
            ["host"] = new JObject
            {
                ["requests"] = host.Requests - hostBefore.Requests,
                ["cacheHits"] = host.CacheHits - hostBefore.CacheHits,
                ["typeThroughHits"] = host.TypeThroughHits - hostBefore.TypeThroughHits,
                ["cacheMisses"] = host.CacheMisses - hostBefore.CacheMisses,
                ["hitRate"] = lookups == 0 ? null : Math.Round(hits / (double)lookups, 3),
                ["modelRequests"] = host.ModelRequests - hostBefore.ModelRequests,
                ["failed"] = host.Failed - hostBefore.Failed,
                ["cancelled"] = host.Cancelled - hostBefore.Cancelled,
                ["dropped"] = host.Dropped - hostBefore.Dropped,
                // These two are counts over the host's five-minute window, not totals since the host started.
                ["promptCountedRequestsInWindow"] = host.PromptCountedRequests,
                ["promptEstimatedRequestsInWindow"] = host.PromptEstimatedRequests,
                ["promptTokensCounted"] = host.PromptTokensCounted - hostBefore.PromptTokensCounted,
                ["promptTokensEstimated"] = host.PromptTokensEstimated - hostBefore.PromptTokensEstimated,
                ["promptTokensMax"] = host.PromptTokensMax,
                ["windowSeconds"] = host.WindowSeconds,
                ["windowFirstTokenP50Ms"] = host.FirstTokenP50Ms,
                ["windowFirstTokenP95Ms"] = host.FirstTokenP95Ms,
                ["windowTotalP50Ms"] = host.TotalP50Ms,
                ["windowTotalP95Ms"] = host.TotalP95Ms,
                ["residentModels"] = new JArray(host.ResidentModels.Select(m => $"{m.ProviderId}/{m.ModelId} ctx {m.ContextLength} vram {m.VramBytes / (1024 * 1024)} MB keep_alive {m.KeepAlive}{(m.IsPinned ? " pinned" : string.Empty)}")),
            },
            ["outbound"] = new JObject
            {
                ["records"] = outbound.Count,
                ["models"] = new JArray(outbound.GroupBy(r => r.ProviderId + "/" + r.ModelId).Select(g => $"{g.Key} x{g.Count()}")),
                ["allLocal"] = outbound.All(r => r.IsLocal),
                ["allExpectedModel"] = expectedModel is null || outbound.All(r => string.Equals(r.ModelId, expectedModel, StringComparison.OrdinalIgnoreCase)),
                ["cacheHitRecords"] = outbound.Count(r => r.CacheHit),
                ["outcomes"] = new JArray(outbound.GroupBy(r => r.Outcome).Select(g => $"{g.Key} x{g.Count()}")),
                ["promptTokensMax"] = outbound.Count == 0 ? 0 : outbound.Max(r => r.PromptTokens),
            },
        };
    }

    /// <summary>Asks for one suggestion at the caret (Pillion: Suggest) and waits for it, so the model is loaded and warm.</summary>
    /// <param name="view">The view.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>How long the suggestion took, or <see langword="null"/> when none came.</returns>
    public static async Task<double?> WarmUpAsync(IWpfTextView view, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var target = CommandTarget(view);
        AutocompleteChecks.Type(view, "using System;");
        Exec(target, VSConstants.VSStd2KCmdID.RETURN, IntPtr.Zero);
        var clock = Stopwatch.StartNew();
        _ = AutocompleteChecks.Suggest(view);
        while (clock.Elapsed < timeout)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (AutocompleteChecks.IsPillionDisplayed(view))
            {
                var ms = clock.Elapsed.TotalMilliseconds;
                Exec(target, VSConstants.VSStd2KCmdID.CANCEL, IntPtr.Zero);
                return ms;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(true);
        }

        return null;
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Inline chat
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// One inline chat request on the real host: selects the scope, opens inline chat with <paramref name="openAsync"/>,
    /// waits for a usable model, types the instruction, presses Enter and times the first preview text, the first complete
    /// line and the complete proposal; then presses Esc and checks the buffer is untouched.
    /// </summary>
    /// <param name="view">The view of the scratch document.</param>
    /// <param name="firstLine">The scope's first line (zero-based).</param>
    /// <param name="lastLine">The scope's last line (zero-based).</param>
    /// <param name="instruction">What to type in the prompt.</param>
    /// <param name="openAsync">Runs Pillion: Inline Chat.</param>
    /// <param name="cancellationToken">Cancels the waits.</param>
    /// <returns>The run's figures.</returns>
    public static async Task<JObject> InlineChatOnceAsync(IWpfTextView view, int firstLine, int lastLine, string instruction, Func<Task> openAsync, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var versionBefore = view.TextBuffer.CurrentSnapshot.Version.VersionNumber;
        _ = InlineChatChecks.SelectLines(view, firstLine, lastLine);
        await openAsync().ConfigureAwait(true);
        var prompt = await InlineChatChecks.WaitForPromptAsync(view, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(true);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var session = prompt.DataContext as InlineChatSession ?? throw new InvalidOperationException("The inline chat prompt's DataContext is not an InlineChatSession.");
        await UntilAsync(() => session.ModelPicker.SelectedModel is { IsAvailable: true }, TimeSpan.FromSeconds(30), "a usable model in the inline chat picker", cancellationToken).ConfigureAwait(true);

        var clock = new Stopwatch();
        double? firstText = null;
        double? firstLineMs = null;
        void OnProposal(object? sender, EventArgs e)
        {
            var text = session.ProposalText;
            if (firstText is null && text.Length > 0)
            {
                firstText = clock.Elapsed.TotalMilliseconds;
            }

            if (firstLineMs is null && text.IndexOf('\n') >= 0)
            {
                firstLineMs = clock.Elapsed.TotalMilliseconds;
            }
        }

        session.ProposalChanged += OnProposal;
        string state;
        double totalMs;
        string proposal;
        try
        {
            prompt.Focus();
            prompt.Text = instruction;
            clock.Start();
            InlineChatChecks.PressKey(prompt, Key.Enter);
            await UntilAsync(() => session.State is InlineChatState.Preview or InlineChatState.Failed or InlineChatState.Explanation or InlineChatState.Closed, TimeSpan.FromSeconds(90), "the inline chat proposal", cancellationToken).ConfigureAwait(true);
            totalMs = clock.Elapsed.TotalMilliseconds;
            state = session.State.ToString();
            proposal = session.ProposalText;
            firstLineMs ??= proposal.Length > 0 ? totalMs : null; // A one-line proposal completes its only line at the end.
        }
        finally
        {
            session.ProposalChanged -= OnProposal;
        }

        var model = session.ModelHeader;
        var error = session.ErrorText;
        InlineChatChecks.PressKey(prompt, Key.Escape);
        await UntilAsync(() => !InlineChatChecks.IsOpen(view), TimeSpan.FromSeconds(10), "Esc to close inline chat", cancellationToken).ConfigureAwait(true);
        return new JObject
        {
            ["instruction"] = instruction,
            ["state"] = state,
            ["model"] = model,
            ["error"] = error,
            ["firstTextMs"] = firstText is { } f ? Math.Round(f) : null,
            ["firstLineMs"] = firstLineMs is { } l ? Math.Round(l) : null,
            ["totalMs"] = Math.Round(totalMs),
            ["proposalLines"] = proposal.Split('\n').Length,
            ["bufferUntouchedAfterEsc"] = view.TextBuffer.CurrentSnapshot.Version.VersionNumber == versionBefore,
            ["proposalFirstLines"] = string.Join("\n", proposal.Split('\n').Take(4)),
        };
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Chat, mentions and the Outbound log
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Sends <paramref name="runs"/> chat messages to the local model through the chat window's input (Enter), each in a
    /// cleared conversation, and times the first visible reply text (the window flushes every 50 ms) and the whole reply.
    /// </summary>
    /// <param name="control">The chat window's content.</param>
    /// <param name="model">The model to pick (its id), or <see langword="null"/> for the picker's choice.</param>
    /// <param name="text">The message.</param>
    /// <param name="runs">How many messages.</param>
    /// <param name="cancellationToken">Cancels the waits.</param>
    /// <param name="local">Whether the model is a local one (or a cloud one).</param>
    /// <returns>The figures.</returns>
    public static async Task<JObject> ChatFirstTokenAsync(FrameworkElement control, string? model, string text, int runs, CancellationToken cancellationToken, bool local = true)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var vm = control.DataContext as ChatViewModel ?? throw new InvalidOperationException("The chat window's DataContext is not a ChatViewModel.");
        var input = control.FindName("Input") as TextBox ?? throw new InvalidOperationException("The chat window has no Input text box.");
        var picked = await PickModelAsync(vm, model, cancellationToken, local).ConfigureAwait(true);
        var first = new List<double>();
        var total = new List<double>();
        var runsArray = new JArray();
        for (var i = 0; i < runs; i++)
        {
            vm.Clear();
            input.Focus();
            input.Text = text;
            input.CaretIndex = text.Length;
            var clock = Stopwatch.StartNew();
            Press(input, Key.Enter);
            double? firstMs = null;
            while (clock.Elapsed < TimeSpan.FromSeconds(120))
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                if (vm.Turns.Count >= 2)
                {
                    var reply = vm.Turns[vm.Turns.Count - 1];
                    if (firstMs is null && reply.Text.Length > 0)
                    {
                        firstMs = clock.Elapsed.TotalMilliseconds;
                    }

                    if (reply.State is not ChatTurnState.Streaming && !vm.IsSending)
                    {
                        break;
                    }
                }

                await Task.Delay(10, cancellationToken).ConfigureAwait(true);
            }

            var last = vm.Turns.Count >= 2 ? vm.Turns[vm.Turns.Count - 1] : null;
            if (firstMs is { } fm)
            {
                first.Add(fm);
            }

            total.Add(clock.Elapsed.TotalMilliseconds);
            runsArray.Add(new JObject
            {
                ["firstTextMs"] = firstMs is { } ms ? Math.Round(ms) : null,
                ["totalMs"] = Math.Round(clock.Elapsed.TotalMilliseconds),
                ["state"] = last?.State.ToString(),
                ["header"] = last?.Header,
                ["error"] = last?.ErrorMessage,
                ["replyStart"] = last is null ? null : new string(last.Text.Take(120).ToArray()),
            });
        }

        vm.Clear();
        return new JObject
        {
            ["model"] = picked,
            ["message"] = text,
            ["firstText"] = Percentiles(first),
            ["total"] = Percentiles(total),
            ["runs"] = runsArray,
        };
    }

    /// <summary>
    /// Pins <c>@file</c> (picked from the picker after typing its start) and <c>@errors</c> (the kind from the picker),
    /// opens the Context Inspector for the message, then sends it and waits for the reply.
    /// </summary>
    /// <param name="control">The chat window's content.</param>
    /// <param name="fileName">The file to pin.</param>
    /// <param name="model">The model to pick, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the waits.</param>
    /// <returns>What was seen.</returns>
    public static async Task<JObject> MentionsAndInspectorAsync(FrameworkElement control, string fileName, string? model, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var vm = control.DataContext as ChatViewModel ?? throw new InvalidOperationException("The chat window's DataContext is not a ChatViewModel.");
        var input = control.FindName("Input") as TextBox ?? throw new InvalidOperationException("The chat window has no Input text box.");
        var picked = await PickModelAsync(vm, model, cancellationToken).ConfigureAwait(true);
        vm.Clear();
        vm.Mentions.Pins.Clear();
        input.Focus();

        // @file: type the start of the name, move to the file in the picker, Enter.
        var typed = "@" + fileName.Substring(0, Math.Min(4, fileName.Length));
        TypeInto(input, typed);
        await UntilAsync(() => vm.Mentions.Picker.IsOpen && vm.Mentions.Picker.Items.Any(i => i.ItemKind == MentionPickerItemKind.File && i.Label == fileName), TimeSpan.FromSeconds(30), "the picker to list " + fileName, cancellationToken).ConfigureAwait(true);
        for (var i = 0; i < 25 && !(vm.Mentions.Picker.SelectedItem is { ItemKind: MentionPickerItemKind.File } s && s.Label == fileName); i++)
        {
            Press(input, Key.Down);
        }

        Press(input, Key.Enter);
        await UntilAsync(() => vm.Mentions.Pins.Count == 1, TimeSpan.FromSeconds(30), "the file pin", cancellationToken).ConfigureAwait(true);

        // @errors and @selection (the caller selected lines in an editor): type the kind, pick it, Enter.
        var notices = new List<string>();
        var steps = new JArray();
        foreach (var kind in MentionKinds)
        {
            var before = vm.Mentions.Pins.Count;
            TypeInto(input, "@" + kind);
            await UntilAsync(() => vm.Mentions.Picker.IsOpen && vm.Mentions.Picker.Items.Any(i => i.ItemKind == MentionPickerItemKind.Kind && i.MentionKind == kind), TimeSpan.FromSeconds(10), "the picker to offer @" + kind, cancellationToken).ConfigureAwait(true);
            var offered = string.Join(", ", vm.Mentions.Picker.Items.Select(i => i.Label));
            for (var i = 0; i < 10 && !(vm.Mentions.Picker.SelectedItem is { ItemKind: MentionPickerItemKind.Kind } k && k.MentionKind == kind); i++)
            {
                Press(input, Key.Down);
            }

            var pickedItem = vm.Mentions.Picker.SelectedItem?.Label;
            Press(input, Key.Enter);
            var how = "picker";
            try
            {
                await UntilAsync(() => vm.Mentions.Pins.Count > before || vm.Mentions.HasNotice, TimeSpan.FromSeconds(10), "the " + kind + " pin", cancellationToken).ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                // The picker left the mention in the input instead of pinning it: resolve the typed mention as Send would.
                how = "typed mention (the picker did not pin it; input was \"" + input.Text + "\")";
                await vm.Mentions.ResolveTextAsync("@" + kind).ConfigureAwait(true);
                await UntilAsync(() => vm.Mentions.Pins.Count > before || vm.Mentions.HasNotice, TimeSpan.FromSeconds(20), "the " + kind + " pin", cancellationToken).ConfigureAwait(true);
            }

            var kindNotice = vm.Mentions.HasNotice ? vm.Mentions.Notice : null;
            if (kindNotice is not null)
            {
                notices.Add(kindNotice);
                vm.Mentions.DismissNoticeCommand.Execute(null);
            }

            steps.Add(new JObject { ["kind"] = kind, ["offered"] = offered, ["picked"] = pickedItem, ["how"] = how, ["pinned"] = vm.Mentions.Pins.Count > before, ["notice"] = kindNotice });
        }

        var pins = new JArray(vm.Mentions.Pins.Select(p => p.DisplayName + " (" + p.TokenBadge + ")"));
        var notice = string.Join(" | ", notices);

        const string Message = "Which compile error does the project have, and in which file?";
        TypeInto(input, Message);
        var inspectClock = Stopwatch.StartNew();
        await vm.InspectAsync().ConfigureAwait(true);
        await UntilAsync(() => vm.Inspector.IsOpen && !vm.Inspector.IsLoading, TimeSpan.FromSeconds(30), "the Context Inspector", cancellationToken).ConfigureAwait(true);
        var inspectMs = inspectClock.Elapsed.TotalMilliseconds;
        var items = new JArray(vm.Inspector.Items.Select(i => i.AutomationName));
        var summary = vm.Inspector.Summary;
        var inspectorError = vm.Inspector.Error;

        var sendClock = Stopwatch.StartNew();
        if (vm.Inspector.SendCommand.CanExecute(null))
        {
            vm.Inspector.SendCommand.Execute(null);
        }
        else
        {
            input.Focus();
            Press(input, Key.Enter);
        }

        await UntilAsync(() => vm.Turns.Count >= 2 && !vm.IsSending && vm.Turns[vm.Turns.Count - 1].State != ChatTurnState.Streaming, TimeSpan.FromSeconds(120), "the reply", cancellationToken).ConfigureAwait(true);
        var reply = vm.Turns[vm.Turns.Count - 1];
        var result = new JObject
        {
            ["model"] = picked,
            ["pins"] = pins,
            ["mentionNotice"] = notice,
            ["mentionSteps"] = steps,
            ["inspectorMs"] = Math.Round(inspectMs),
            ["inspectorItems"] = items,
            ["inspectorSummary"] = summary,
            ["inspectorError"] = inspectorError,
            ["replyState"] = reply.State.ToString(),
            ["replyHeader"] = reply.Header,
            ["replyMs"] = Math.Round(sendClock.Elapsed.TotalMilliseconds),
            ["replyStart"] = new string(reply.Text.Take(200).ToArray()),
        };
        vm.Mentions.Pins.Clear();
        vm.Clear();
        return result;
    }

    /// <summary>Summarises the host's Outbound records by task class since a moment.</summary>
    /// <param name="sinceUtc">The start of the run.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The summary.</returns>
    public static async Task<JObject> OutboundByTaskClassAsync(DateTime sinceUtc, CancellationToken cancellationToken)
    {
        var records = (await OutboundAsync(cancellationToken).ConfigureAwait(false)).Where(r => r.TimestampUtc >= sinceUtc).ToList();
        var byClass = new JObject();
        foreach (var group in records.GroupBy(r => r.TaskClass))
        {
            var newest = group.Last();
            byClass[group.Key.ToString()] = new JObject
            {
                ["records"] = group.Count(),
                ["local"] = group.Count(r => r.IsLocal),
                ["models"] = new JArray(group.Select(r => r.ProviderId + "/" + r.ModelId).Distinct()),
                ["outcomes"] = new JArray(group.GroupBy(r => r.Outcome).Select(g => $"{g.Key} x{g.Count()}")),
                ["example"] = $"{newest.TimestampUtc:HH:mm:ss.fff} {newest.TaskClass} {newest.ProviderId}/{newest.ModelId} local={newest.IsLocal} prompt {newest.PromptTokens} completion {newest.CompletionTokens} context items {newest.ContextItems} redactions {newest.RedactionCount} {newest.DurationMs} ms {newest.Outcome}",
            };
        }

        return new JObject
        {
            ["total"] = records.Count,
            ["byTaskClass"] = byClass,
            ["hasAutocomplete"] = records.Any(r => r.TaskClass == TaskClass.Autocomplete),
            ["hasInlineEdit"] = records.Any(r => r.TaskClass == TaskClass.InlineEdit),
            ["hasChat"] = records.Any(r => r.TaskClass == TaskClass.Chat),
        };
    }

    private static async Task<string> PickModelAsync(ChatViewModel vm, string? model, CancellationToken cancellationToken, bool local = true)
    {
        await UntilAsync(() => vm.ModelPicker.SelectedModel is { IsAvailable: true } && vm.IsHostUsable, TimeSpan.FromSeconds(60), "a usable model in the chat picker", cancellationToken).ConfigureAwait(true);
        if (model is not null)
        {
            var match = vm.ModelPicker.Models.FirstOrDefault(m => !m.IsAuto && m.IsLocal == local && m.IsAvailable && string.Equals(m.ModelId, model, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                throw new InvalidOperationException($"No available {(local ? "local" : "cloud")} model {model} in the chat picker: {string.Join(" | ", vm.ModelPicker.Models.Select(m => m.AutomationName))}");
            }

            vm.ModelPicker.SelectedModel = match;
        }

        return vm.ModelPicker.SelectedModel!.DisplayText;
    }

    private static void TypeInto(TextBox input, string text)
    {
        // The mentions code-behind follows the text and the caret, as when typing.
        input.Text = text;
        input.CaretIndex = text.Length;
    }

    private static void Press(TextBox input, Key key)
    {
        var source = PresentationSource.FromVisual(input) ?? throw new InvalidOperationException("The chat input is not shown.");
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        input.RaiseEvent(preview);
        if (!preview.Handled)
        {
            input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });
        }
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>Nearest-rank p50 and p95, with the count, mean and max, in milliseconds.</summary>
    /// <param name="samples">The samples in milliseconds.</param>
    /// <returns>The summary.</returns>
    public static JObject Percentiles(IReadOnlyCollection<double> samples)
    {
        if (samples.Count == 0)
        {
            return new JObject { ["count"] = 0 };
        }

        var sorted = samples.OrderBy(v => v).ToArray();
        double Rank(double p) => sorted[Math.Max(0, Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1))];
        return new JObject
        {
            ["count"] = sorted.Length,
            ["p50"] = Math.Round(Rank(0.50), 3),
            ["p95"] = Math.Round(Rank(0.95), 3),
            ["mean"] = Math.Round(sorted.Average(), 3),
            ["max"] = Math.Round(sorted[sorted.Length - 1], 3),
        };
    }

    private static JObject Durations(DurationPercentiles percentiles) => new()
    {
        ["count"] = percentiles.Count,
        ["p50"] = percentiles.P50 is { } p50 ? Math.Round(p50.TotalMilliseconds) : null,
        ["p95"] = percentiles.P95 is { } p95 ? Math.Round(p95.TotalMilliseconds) : null,
    };

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static async Task UntilAsync(Func<bool> condition, TimeSpan timeout, string what, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (condition())
            {
                return;
            }

            if (clock.Elapsed > timeout)
            {
                throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0} s waiting for {what}.");
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(true);
        }
    }
}

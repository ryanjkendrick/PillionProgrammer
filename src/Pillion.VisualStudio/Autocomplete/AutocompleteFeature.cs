using System;
using System.Threading;
using System.Threading.Tasks;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Autocomplete;

namespace Pillion.VisualStudio.Autocomplete;

/// <summary>
/// The package-owned part of autocomplete: the feature flag and settings, the request engine shared by every view, the
/// latest IntelliCode check, suggestion coexistence, and the local telemetry (the metrics log and the five-minute Output pane summary). Created
/// when the package loads; the MEF parts reach it through <see cref="PillionPackage.Instance"/>.
/// </summary>
internal sealed class AutocompleteFeature : IDisposable
{
    /// <summary>The least time between two status bar updates caused by the autocomplete indicator.</summary>
    private static readonly TimeSpan StatusBarInterval = TimeSpan.FromSeconds(2);

    /// <summary>How long Show Status waits for the host's counters.</summary>
    private static readonly TimeSpan HostCountersTimeout = TimeSpan.FromSeconds(2);

    private readonly CoalescingNotifier _statusNotifier;
    private volatile IntelliCodeStatus _intelliCode = IntelliCodeStatus.NotRequired;

    /// <summary>Initializes a new instance of the <see cref="AutocompleteFeature"/> class.</summary>
    /// <param name="connection">The connection to Pillion Host.</param>
    /// <param name="log">The extension log.</param>
    public AutocompleteFeature(IHostConnection connection, PillionLog log)
    {
        Log = log;
        Connection = connection;
        Availability = new AutocompleteAvailability(connection, log);
        Engine = new AutocompleteEngine(connection, Availability, SystemClock.Instance, log);
        Reporter = new AutocompleteMetricsReporter(
            Engine,
            new AutocompleteMetricsLogWriter(PillionLog.DefaultDirectory),
            AutocompleteMetricsReporter.ParseInterval(Environment.GetEnvironmentVariable(AutocompleteMetricsReporter.IntervalVariable)));
        _statusNotifier = new CoalescingNotifier(SystemClock.Instance, StatusBarInterval, () => StatusChanged?.Invoke(this, EventArgs.Empty));
        Engine.Metrics.IndicatorChanged += (_, _) => _statusNotifier.Poke();
        Coexistence = new CoexistenceFeature(this);
    }

    /// <summary>Raised on a pool thread, at most every two seconds, when the status bar indicator changed (a request is waiting for the model, or autocomplete became degraded or recovered).</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Gets the extension log.</summary>
    public PillionLog Log { get; }

    /// <summary>Gets the connection to Pillion Host.</summary>
    public IHostConnection Connection { get; }

    /// <summary>Gets suggestion coexistence: the Copilot and IntelliCode detection, whose proposals were seen, and the info bar.</summary>
    public CoexistenceFeature Coexistence { get; }

    /// <summary>Gets the feature flag and settings.</summary>
    public AutocompleteAvailability Availability { get; }

    /// <summary>Gets the request engine.</summary>
    public AutocompleteEngine Engine { get; }

    /// <summary>Gets the writer of the metrics log and the Output pane summary.</summary>
    public AutocompleteMetricsReporter Reporter { get; }

    /// <summary>Gets or sets the latest IntelliCode check result.</summary>
    public IntelliCodeStatus IntelliCode
    {
        get => _intelliCode;
        set => _intelliCode = value;
    }

    /// <summary>Gets what the status bar adds after the host state: empty, ", waiting for suggestion" or ", suggestions slow".</summary>
    public string StatusBarSuffix => AutocompleteMetricsText.StatusSuffix(Engine.Metrics.Indicator);

    /// <summary>
    /// Gets the text for Pillion: Show Status: the "Autocomplete" section (this extension's figures and the host's
    /// beside them), whether autocomplete is switched off, and the IntelliCode state.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the wait for the host.</param>
    /// <returns>The text.</returns>
    public async Task<string> GetStatusTextAsync(CancellationToken cancellationToken)
    {
        var host = await Engine.TryGetHostCountersAsync(HostCountersTimeout, cancellationToken).ConfigureAwait(false);
        return AutocompleteMetricsText.Describe(Engine.Metrics.Snapshot(), host)
            + (Availability.Settings.Enabled ? string.Empty : Environment.NewLine + "Autocomplete is disabled by settings or policy")
            + (IntelliCodeRequirement.Describe(IntelliCode) is { } intelliCode ? Environment.NewLine + intelliCode : string.Empty);
    }

    /// <summary>Starts following the host's settings and the five-minute report.</summary>
    public void Start()
    {
        Availability.Start();
        Reporter.Start();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Reporter.Dispose();
        Availability.Dispose();
    }
}

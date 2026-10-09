using System;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Autocomplete;

namespace Pillion.VisualStudio.Autocomplete;

/// <summary>
/// The package-owned part of autocomplete: the feature flag and settings, the request engine shared by every view, and
/// the latest IntelliCode check. Created when the package loads; the MEF parts reach it through <see cref="PillionPackage.Instance"/>.
/// </summary>
internal sealed class AutocompleteFeature : IDisposable
{
    private volatile IntelliCodeStatus _intelliCode = IntelliCodeStatus.NotRequired;

    /// <summary>Initializes a new instance of the <see cref="AutocompleteFeature"/> class.</summary>
    /// <param name="connection">The connection to Pillion Host.</param>
    /// <param name="log">The extension log.</param>
    public AutocompleteFeature(IHostConnection connection, PillionLog log)
    {
        Log = log;
        Availability = new AutocompleteAvailability(connection, log);
        Engine = new AutocompleteEngine(connection, Availability, SystemClock.Instance, log);
    }

    /// <summary>Gets the extension log.</summary>
    public PillionLog Log { get; }

    /// <summary>Gets the feature flag and settings.</summary>
    public AutocompleteAvailability Availability { get; }

    /// <summary>Gets the request engine.</summary>
    public AutocompleteEngine Engine { get; }

    /// <summary>Gets or sets the latest IntelliCode check result.</summary>
    public IntelliCodeStatus IntelliCode
    {
        get => _intelliCode;
        set => _intelliCode = value;
    }

    /// <summary>Gets the line for Pillion: Show Status (latency counters, degradation and the IntelliCode state).</summary>
    public string StatusText =>
        Engine.Latency.Snapshot()
        + (Availability.Settings.Enabled ? string.Empty : "; autocomplete is disabled by settings or policy")
        + (IntelliCodeRequirement.Describe(IntelliCode) is { } intelliCode ? Environment.NewLine + intelliCode : string.Empty);

    /// <summary>Starts following the host's settings.</summary>
    public void Start() => Availability.Start();

    /// <inheritdoc/>
    public void Dispose() => Availability.Dispose();
}

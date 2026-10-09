using System;
using System.Collections.Generic;
using System.Linq;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// Describes a <c>settingsChanged</c> notification from Pillion Host: the top-level sections of <c>settings.json</c>
/// that changed (for example <c>routing</c> and <c>cloudSwitches</c>).
/// </summary>
public sealed class HostSettingsChangedEventArgs : EventArgs
{
    /// <summary>Initializes a new instance of the <see cref="HostSettingsChangedEventArgs"/> class.</summary>
    /// <param name="sections">The names of the sections that changed.</param>
    public HostSettingsChangedEventArgs(IReadOnlyList<string>? sections)
    {
        Sections = sections ?? Array.Empty<string>();
    }

    /// <summary>Gets the names of the top-level sections that changed.</summary>
    public IReadOnlyList<string> Sections { get; }

    /// <summary>Returns whether any of the given sections changed (names are compared ignoring case).</summary>
    /// <param name="names">The section names to look for.</param>
    /// <returns><see langword="true"/> when at least one of them is in <see cref="Sections"/>.</returns>
    public bool Affects(params string[] names) =>
        names is not null && Sections.Any(section => names.Any(name => string.Equals(section, name, StringComparison.OrdinalIgnoreCase)));
}

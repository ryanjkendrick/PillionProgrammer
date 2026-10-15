using System;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// Describes an <c>indexStatusChanged</c> notification from Pillion Host (contract 1.3): the latest state of the workspace
/// index. The host sends it at most twice a second.
/// </summary>
public sealed class HostIndexStatusChangedEventArgs : EventArgs
{
    /// <summary>Initializes a new instance of the <see cref="HostIndexStatusChangedEventArgs"/> class.</summary>
    /// <param name="status">The index status the host reported; <see langword="null"/> is read as an empty status.</param>
    public HostIndexStatusChangedEventArgs(IndexStatus? status)
    {
        Status = status ?? new IndexStatus();
    }

    /// <summary>Gets the index status the host reported.</summary>
    public IndexStatus Status { get; }
}

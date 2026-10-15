using System;
using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>
/// What is open in Visual Studio, as the VSIX relays it to the host with <see cref="IContextService.SetWorkspaceAsync"/>:
/// the solution file and its projects, or an opened folder. The host resolves the repository roots from these paths (the git
/// top-level directory of each, or the opened folder) and indexes them. Added in contract 1.3 (work item 2.9).
/// </summary>
/// <remarks>An empty description (no solution, no projects, no folder) means nothing is open: the host closes its indexes.</remarks>
[DataContract]
public sealed class WorkspaceDescription
{
    /// <summary>Gets or sets the full path of the open solution file (<c>.sln</c> or <c>.slnx</c>), or <see langword="null"/> in folder mode or when nothing is open.</summary>
    [DataMember(Order = 0)]
    public string? SolutionPath { get; set; }

    /// <summary>Gets or sets the full paths of the solution's loaded projects (project files, or their folders when a project has no file).</summary>
    [DataMember(Order = 1)]
    public string[] ProjectPaths { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the full path of the folder opened with Open Folder, or <see langword="null"/> when a solution is open.</summary>
    [DataMember(Order = 2)]
    public string? FolderPath { get; set; }
}

/// <summary>
/// What the IDE is busy with, as a set of flags: the argument of the <c>ideActivityChanged</c> notification (VSIX to host,
/// contract 1.3, work item 2.9). The host pauses background indexing while any of these is set (design sections 7 and 13).
/// An integer on the wire.
/// </summary>
[Flags]
public enum IdeActivity
{
    /// <summary>Nothing that pauses indexing.</summary>
    None = 0,

    /// <summary>A solution build, rebuild or clean is running.</summary>
    Build = 1,

    /// <summary>The debugger is attached (debug mode or break mode).</summary>
    Debug = 2,

    /// <summary>A test run is in progress.</summary>
    Test = 4,
}

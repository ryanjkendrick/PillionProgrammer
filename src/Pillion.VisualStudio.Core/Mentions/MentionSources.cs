using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pillion.VisualStudio.Core.Mentions;

/// <summary>The files of the open solution or folder, as the picker matches them.</summary>
public sealed class WorkspaceFileList
{
    /// <summary>Initializes a new instance of the <see cref="WorkspaceFileList"/> class.</summary>
    /// <param name="roots">The solution or folder roots (the solution's folder and each project's folder, or the open folder).</param>
    /// <param name="files">The full paths of the files.</param>
    public WorkspaceFileList(IReadOnlyList<string> roots, IReadOnlyList<string> files)
    {
        Roots = roots ?? [];
        Files = files ?? [];
    }

    /// <summary>Gets an empty list (nothing is open).</summary>
    public static WorkspaceFileList Empty { get; } = new([], []);

    /// <summary>Gets the solution or folder roots.</summary>
    public IReadOnlyList<string> Roots { get; }

    /// <summary>Gets the full paths of the files.</summary>
    public IReadOnlyList<string> Files { get; }
}

/// <summary>
/// The solution or folder file list for <c>@file</c> and <c>@folder</c> (phase 1 has no index). Cached by the implementation
/// and refreshed on solution events; <c>bin</c>, <c>obj</c>, <c>.git</c>, <c>node_modules</c> and <c>.vs</c> are left out.
/// </summary>
public interface IWorkspaceFiles
{
    /// <summary>Raised, on any thread, when <see cref="Current"/> changed (a solution, project or folder opened or closed).</summary>
    event EventHandler? Changed;

    /// <summary>Gets the list as last collected (empty before the first collection). Safe to read on any thread.</summary>
    WorkspaceFileList Current { get; }

    /// <summary>Gets the list, collecting it first when it is missing or stale.</summary>
    /// <param name="cancellationToken">A token that cancels the collection.</param>
    /// <returns>The list.</returns>
    Task<WorkspaceFileList> GetAsync(CancellationToken cancellationToken);
}

/// <summary>The file open in the editor and the text the chat request carries from it.</summary>
public sealed class CurrentFileContext
{
    /// <summary>Initializes a new instance of the <see cref="CurrentFileContext"/> class.</summary>
    /// <param name="path">The full path of the file.</param>
    /// <param name="region">The selection, else the lines around the caret.</param>
    public CurrentFileContext(string path, string region)
    {
        Path = path ?? string.Empty;
        Region = region ?? string.Empty;
    }

    /// <summary>Gets the full path of the file.</summary>
    public string Path { get; }

    /// <summary>Gets the selection, else the 60 lines around the caret.</summary>
    public string Region { get; }
}

/// <summary>
/// What only Visual Studio knows, for <c>@selection</c>, <c>@clipboard</c>, <c>@errors</c> and the current file region. The
/// implementation switches to the UI thread itself; callers may be on any thread.
/// </summary>
public interface IChatEditorContext
{
    /// <summary>Gets the selected text in the active editor.</summary>
    /// <param name="cancellationToken">A token that cancels the call.</param>
    /// <returns>The text, or <see langword="null"/> when nothing is selected or no editor is open.</returns>
    Task<string?> GetSelectionAsync(CancellationToken cancellationToken);

    /// <summary>Gets the active editor's file and region: the selection, else 60 lines around the caret.</summary>
    /// <param name="cancellationToken">A token that cancels the call.</param>
    /// <returns>The file and region, or <see langword="null"/> when no editor with a file is open.</returns>
    Task<CurrentFileContext?> GetCurrentFileAsync(CancellationToken cancellationToken);

    /// <summary>Gets the clipboard's text.</summary>
    /// <param name="cancellationToken">A token that cancels the call.</param>
    /// <returns>The text, or <see langword="null"/> when the clipboard holds none.</returns>
    Task<string?> GetClipboardTextAsync(CancellationToken cancellationToken);

    /// <summary>Gets the solution's diagnostics as the JSON lines <c>@errors</c> sends (see <see cref="ErrorsPayload"/>).</summary>
    /// <param name="cancellationToken">A token that cancels the call.</param>
    /// <returns>The JSON lines; empty when there are no diagnostics.</returns>
    Task<string> GetErrorsPayloadAsync(CancellationToken cancellationToken);
}

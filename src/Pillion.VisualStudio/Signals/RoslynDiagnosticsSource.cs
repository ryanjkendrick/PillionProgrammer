using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Signals;

namespace Pillion.VisualStudio.Signals;

/// <summary>
/// Reads Roslyn's compiler diagnostics (syntax and semantic) for an open C# or Visual Basic document from Visual Studio's
/// workspace. The workspace is Visual Studio's <c>VisualStudioWorkspace</c>, a MEF export of
/// Microsoft.VisualStudio.LanguageServices; that assembly is not in the 17.14 SDK package graph, so the export is looked
/// up by name through <see cref="IComponentModel"/> and used as its base type <see cref="Workspace"/> (compile-time
/// reference to Microsoft.CodeAnalysis.Workspaces.Common only, like the autocomplete code). Analyzer diagnostics are not
/// included here; the Error List carries them. When the workspace cannot be found, <see cref="UnavailableReason"/> says so
/// and callers use the Error List only.
/// </summary>
internal sealed class RoslynDiagnosticsSource : IRoslynDiagnosticsSource
{
    private const string WorkspaceTypeName = "Microsoft.VisualStudio.LanguageServices.VisualStudioWorkspace";
    private const string WorkspaceAssemblyName = "Microsoft.VisualStudio.LanguageServices";
    private const int MaxDiagnostics = 500;

    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private readonly PillionLog? _log;
    private readonly object _gate = new();
    private Workspace? _workspace;
    private string? _unavailableReason;
    private DateTime _nextAttemptUtc = DateTime.MinValue;

    /// <summary>Initializes a new instance of the <see cref="RoslynDiagnosticsSource"/> class.</summary>
    /// <param name="log">The extension log, or <see langword="null"/>.</param>
    public RoslynDiagnosticsSource(PillionLog? log) => _log = log;

    /// <inheritdoc/>
    public string? UnavailableReason
    {
        get
        {
            lock (_gate)
            {
                return _unavailableReason;
            }
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DiagnosticInfo>?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var workspace = await GetWorkspaceAsync().ConfigureAwait(false);
        if (workspace is null)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadTimeout);
        try
        {
            return await ReadDocumentAsync(workspace, path, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The compilation was not ready in time: the Error List's entries stand in.
            _log?.Debug("signals.roslyn.timeout", "Roslyn diagnostics timed out.", ("path", path));
            return null;
        }
#pragma warning disable CA1031 // A Roslyn failure must never break the query; the Error List answers instead.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _log?.Warning("signals.roslyn.failed", "Reading Roslyn diagnostics failed.", ("error", ex.GetType().Name), ("detail", ex.Message));
            return null;
        }
    }

    private static async Task<IReadOnlyList<DiagnosticInfo>?> ReadDocumentAsync(Workspace workspace, string path, CancellationToken cancellationToken)
    {
        var solution = workspace.CurrentSolution;
        var ids = solution.GetDocumentIdsWithFilePath(path);
        if (ids.IsDefaultOrEmpty)
        {
            return null;
        }

        var document = solution.GetDocument(ids[0]);
        if (document is null)
        {
            return null;
        }

        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (model is null)
        {
            // The language has no compilation (for example a text file in a project).
            return null;
        }

        var result = new List<DiagnosticInfo>();
        foreach (var diagnostic in model.GetDiagnostics(cancellationToken: cancellationToken))
        {
            if (diagnostic.Severity == DiagnosticSeverity.Hidden)
            {
                continue;
            }

            var position = diagnostic.Location.GetLineSpan().StartLinePosition;
            result.Add(new DiagnosticInfo(
                path,
                position.Line + 1,
                position.Character + 1,
                diagnostic.Severity switch
                {
                    DiagnosticSeverity.Error => IdeSeverity.Error,
                    DiagnosticSeverity.Warning => IdeSeverity.Warning,
                    _ => IdeSeverity.Info,
                },
                diagnostic.Id,
                diagnostic.GetMessage(CultureInfo.InvariantCulture),
                document.Project.Name,
                Core.Signals.DiagnosticSource.Roslyn));
            if (result.Count >= MaxDiagnostics)
            {
                break;
            }
        }

        return result;
    }

    private async Task<Workspace?> GetWorkspaceAsync()
    {
        lock (_gate)
        {
            if (_workspace is not null)
            {
                return _workspace;
            }

            if (DateTime.UtcNow < _nextAttemptUtc)
            {
                return null;
            }

            _nextAttemptUtc = DateTime.UtcNow + RetryInterval;
        }

        var (workspace, reason) = await ResolveAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _workspace = workspace;
            _unavailableReason = reason;
        }

        if (workspace is null)
        {
            _log?.Info("signals.roslyn.unavailable", "Roslyn diagnostics are unavailable.", ("reason", reason));
        }

        return workspace;
    }

    private static async Task<(Workspace? Workspace, string? Reason)> ResolveAsync()
    {
        try
        {
            var componentModel = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SComponentModel)).ConfigureAwait(false) as IComponentModel;
            if (componentModel is null)
            {
                return (null, "the component model is not available");
            }

            var workspaceType = FindWorkspaceType();
            if (workspaceType is null)
            {
                return (null, WorkspaceAssemblyName + " is not loaded");
            }

            var getService = typeof(IComponentModel).GetMethod(nameof(IComponentModel.GetService))!.MakeGenericMethod(workspaceType);
            return getService.Invoke(componentModel, null) is Workspace workspace
                ? (workspace, null)
                : (null, "the Visual Studio workspace is not exported");
        }
#pragma warning disable CA1031 // Without the workspace the Error List answers.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return (null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static Type? FindWorkspaceType()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => string.Equals(a.GetName().Name, WorkspaceAssemblyName, StringComparison.Ordinal));
        if (assembly is null)
        {
            try
            {
                assembly = Assembly.Load(WorkspaceAssemblyName);
            }
            catch (Exception ex) when (ex is System.IO.IOException or BadImageFormatException)
            {
                return null;
            }
        }

        return assembly.GetType(WorkspaceTypeName, throwOnError: false);
    }
}

using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;

namespace Pillion.VisualStudio.Signals;

/// <summary>
/// Finds Visual Studio's Roslyn workspace (<c>VisualStudioWorkspace</c>, a MEF export of
/// Microsoft.VisualStudio.LanguageServices). That assembly is not in the 17.14 SDK package graph, so the export is looked
/// up by name through <see cref="IComponentModel"/> and used as its base type <see cref="Workspace"/> (compile-time
/// reference to Microsoft.CodeAnalysis.Workspaces.Common only). Used by the Roslyn diagnostics and the workspace queries.
/// </summary>
internal static class VisualStudioWorkspaceLocator
{
    private const string WorkspaceTypeName = "Microsoft.VisualStudio.LanguageServices.VisualStudioWorkspace";
    private const string WorkspaceAssemblyName = "Microsoft.VisualStudio.LanguageServices";

    /// <summary>Looks the workspace up. Call it off the UI thread; the component model may visit it once to create itself.</summary>
    /// <returns>The workspace, or <see langword="null"/> and why it is not available.</returns>
    public static async Task<(Workspace? Workspace, string? Reason)> ResolveAsync()
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
#pragma warning disable CA1031 // Without the workspace the callers fall back (the Error List, the index's edges).
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

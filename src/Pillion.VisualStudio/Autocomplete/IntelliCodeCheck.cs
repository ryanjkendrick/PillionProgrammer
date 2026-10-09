using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Internal.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Language.Suggestions;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Pillion.VisualStudio.Core.Autocomplete;

namespace Pillion.VisualStudio.Autocomplete;

/// <summary>
/// The IntelliCode run-time check on Visual Studio 2022 (17.x): on 17.x the proposal broker and suggestion service
/// that draw ghost text come from IntelliCode, and its "Automatically generate code completions in the Editor" option
/// switches them off for every provider. The check looks for the IntelliCode package and its suggestion service, and
/// reads the option from the settings manager (<see cref="IntelliCodeRequirement.ShowCompletionsSetting"/>). On 18.x
/// the platform supplies the service, so there is nothing to check.
/// </summary>
internal static class IntelliCodeCheck
{
    /// <summary>Gets the major version of the running devenv.exe (17 for Visual Studio 2022, 18 for 2026).</summary>
    public static int VisualStudioMajorVersion { get; } = ReadMajorVersion();

    /// <summary>Runs the check. Switches to the UI thread for the shell services; quick.</summary>
    /// <param name="package">The package (service provider).</param>
    /// <param name="cancellationToken">A token that cancels the check.</param>
    /// <returns>The status and the raw option value (for the log).</returns>
    public static async Task<(IntelliCodeStatus Status, string Detail)> RunAsync(AsyncPackage package, CancellationToken cancellationToken)
    {
        var major = VisualStudioMajorVersion;
        if (major >= 18 || major == 0)
        {
            return (IntelliCodeStatus.NotRequired, "Visual Studio " + major);
        }

        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var packageInstalled = false;
        if (await package.GetServiceAsync(typeof(SVsShell)).ConfigureAwait(true) is IVsShell shell)
        {
            var guid = new Guid(IntelliCodeRequirement.PackageGuid);
            packageInstalled = Microsoft.VisualStudio.ErrorHandler.Succeeded(shell.IsPackageInstalled(ref guid, out var installed)) && installed != 0;
        }

        var serviceExported = false;
        if (await package.GetServiceAsync(typeof(SComponentModel)).ConfigureAwait(true) is IComponentModel componentModel)
        {
            try
            {
                serviceExported = componentModel.DefaultExportProvider.GetExports<SuggestionServiceBase>().Any();
            }
#pragma warning disable CA1031 // A composition failure means the service is not usable.
            catch (Exception)
#pragma warning restore CA1031
            {
                serviceExported = false;
            }
        }

        object? stored = null;
        var readable = false;
        if (await package.GetServiceAsync(typeof(SVsSettingsPersistenceManager)).ConfigureAwait(true) is ISettingsManager settings)
        {
            try
            {
                var result = settings.TryGetValue(IntelliCodeRequirement.ShowCompletionsSetting, out object value);
                readable = true;
                stored = result == GetValueResult.Success ? value : null;
            }
#pragma warning disable CA1031 // An unreadable option is treated as on.
            catch (Exception)
#pragma warning restore CA1031
            {
                readable = false;
            }
        }

        var completionsOn = readable ? IntelliCodeRequirement.InterpretShowCompletions(stored) : null;
        var status = IntelliCodeRequirement.Evaluate(major, packageInstalled && serviceExported, completionsOn);
        var detail = $"Visual Studio {major}, IntelliCode package {(packageInstalled ? "installed" : "missing")}, suggestion service {(serviceExported ? "present" : "missing")}, "
            + $"{IntelliCodeRequirement.ShowCompletionsSetting}={(readable ? stored?.ToString() ?? "<unset>" : "<unreadable>")}";
        return (status, detail);
    }

    private static int ReadMajorVersion()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.MainModule?.FileVersionInfo.FileMajorPart ?? 0;
        }
#pragma warning disable CA1031 // Unknown version: treat as the newest, where no check is needed.
        catch (Exception)
#pragma warning restore CA1031
        {
            return 0;
        }
    }
}

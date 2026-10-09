using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading.Tasks;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>Finds the Pillion.Host.exe processes started by this devenv.exe (the test runs inside it).</summary>
public static class HostProcesses
{
    /// <summary>Returns the process ids of the Pillion.Host.exe children of the current process.</summary>
    /// <returns>The ids, possibly empty.</returns>
    public static Task<IReadOnlyList<int>> FindChildrenAsync() => Task.Run(FindChildren);

    private static IReadOnlyList<int> FindChildren()
    {
        using var self = Process.GetCurrentProcess();
        var query = $"SELECT ProcessId FROM Win32_Process WHERE Name = 'Pillion.Host.exe' AND ParentProcessId = {self.Id}";
        using var searcher = new ManagementObjectSearcher(query);
        using var results = searcher.Get();
        return results.Cast<ManagementObject>()
            .Select(o =>
            {
                using (o)
                {
                    return Convert.ToInt32(o["ProcessId"], System.Globalization.CultureInfo.InvariantCulture);
                }
            })
            .ToList();
    }
}

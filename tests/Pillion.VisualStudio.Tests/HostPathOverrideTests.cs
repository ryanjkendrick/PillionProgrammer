using System;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Tests;

/// <summary>The host path override the Visual Studio integration tests use to run against the fake host.</summary>
public sealed class HostPathOverrideTests
{
    [Fact]
    public void Override_ReplacesTheConfiguredPath_OnlyWhileSet()
    {
        var previous = Environment.GetEnvironmentVariable(HostProcessLauncher.HostPathOverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(HostProcessLauncher.HostPathOverrideVariable, null);
            Assert.Equal(@"C:\ext\Host\Pillion.Host.exe", HostProcessLauncher.ResolveHostPath(@"C:\ext\Host\Pillion.Host.exe"));

            Environment.SetEnvironmentVariable(HostProcessLauncher.HostPathOverrideVariable, @"C:\repo\tests\Pillion.FakeHost.exe");
            Assert.Equal(@"C:\repo\tests\Pillion.FakeHost.exe", HostProcessLauncher.ResolveHostPath(@"C:\ext\Host\Pillion.Host.exe"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(HostProcessLauncher.HostPathOverrideVariable, previous);
        }
    }
}

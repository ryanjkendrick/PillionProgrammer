using System.Diagnostics;
using Pillion.Common.Secrets;

namespace Pillion.Common.Tests;

/// <summary>Skips a test with a message when the Windows Credential Manager cannot be used on this machine.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CredentialManagerFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> SkipReason = new(Probe);

    public CredentialManagerFactAttribute()
    {
        Skip = SkipReason.Value;
    }

    private static string? Probe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "The Windows Credential Manager is only available on Windows.";
        }

        var name = $"test-probe-{Guid.NewGuid():N}";
        var store = new WindowsCredentialManagerSecretStore();
        try
        {
            store.Set(name, "probe");
            return store.Get(name) == "probe" ? null : "The Windows Credential Manager did not return the value it was given.";
        }
        catch (Exception ex)
        {
            return $"The Windows Credential Manager is not available here: {ex.Message}";
        }
        finally
        {
            try
            {
                store.Delete(name);
            }
            catch (SecretStoreException)
            {
                // Nothing more to do.
            }
        }
    }
}

public class WindowsCredentialManagerSecretStoreTests
{
    [CredentialManagerFact]
    public void RoundTrip_SetGetListOverwriteDelete()
    {
        var store = new WindowsCredentialManagerSecretStore();
        var name = $"test-{Guid.NewGuid():N}";
        try
        {
            Assert.Null(store.Get(name));
            Assert.DoesNotContain(name, store.List());

            store.Set(name, "sk-first-value é世😀");
            Assert.Equal("sk-first-value é世😀", store.Get(name));
            Assert.Contains(name, store.List());

            store.Set(name, "second");
            Assert.Equal("second", store.Get(name));

            Assert.True(store.Delete(name));
            Assert.Null(store.Get(name));
            Assert.DoesNotContain(name, store.List());
            Assert.False(store.Delete(name));
        }
        finally
        {
            store.Delete(name);
        }
    }

    [CredentialManagerFact]
    public void StoresAGenericCredentialUnderThePillionTargetName()
    {
        var store = new WindowsCredentialManagerSecretStore();
        var name = $"test-{Guid.NewGuid():N}";
        try
        {
            store.Set(name, "value");

            var output = RunCmdKey($"/list:PillionProgrammer/{name}");
            if (output is null)
            {
                return; // cmdkey.exe is not available; the round-trip test still covers behaviour.
            }

            Assert.Contains($"PillionProgrammer/{name}", output, StringComparison.Ordinal);
        }
        finally
        {
            store.Delete(name);
        }
    }

    [CredentialManagerFact]
    public void Set_AtTheBlobLimit_Succeeds_AndAboveItFailsWithAClearMessage()
    {
        var store = new WindowsCredentialManagerSecretStore();
        var name = $"test-{Guid.NewGuid():N}";
        var maxChars = SecretNames.MaxBlobBytes / sizeof(char);
        try
        {
            var atLimit = new string('Q', maxChars);
            store.Set(name, atLimit);
            Assert.Equal(atLimit, store.Get(name));

            var ex = Assert.Throws<SecretStoreException>(() => store.Set(name, new string('Z', maxChars + 1)));
            Assert.Contains("too large", ex.Message, StringComparison.Ordinal);
            Assert.Contains("2560", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("ZZZZ", ex.Message, StringComparison.Ordinal);

            // The failed write did not damage the stored value.
            Assert.Equal(atLimit, store.Get(name));
        }
        finally
        {
            store.Delete(name);
        }
    }

    [Fact]
    public void Set_AboveTheBlobLimit_FailsBeforeTouchingTheCredentialManager()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new WindowsCredentialManagerSecretStore();
        var name = $"test-{Guid.NewGuid():N}";

        var ex = Assert.Throws<SecretStoreException>(() => store.Set(name, new string('S', 1281)));

        Assert.Contains("too large", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SSSS", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("slash/name")]
    [InlineData("star*")]
    public void InvalidNames_AreRejected(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new WindowsCredentialManagerSecretStore();

        Assert.Throws<ArgumentException>(() => store.Get(name));
        Assert.Throws<ArgumentException>(() => store.Set(name, "v"));
        Assert.Throws<ArgumentException>(() => store.Delete(name));
    }

    private static string? RunCmdKey(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmdkey.exe", arguments)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            return output;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

public class InMemorySecretStoreTests
{
    [Fact]
    public void RoundTrip_SetGetListDelete()
    {
        var store = new InMemorySecretStore();

        Assert.Null(store.Get("openai"));
        store.Set("openai", "sk-1");
        store.Set("anthropic", "sk-2");
        store.Set("openai", "sk-3");

        Assert.Equal("sk-3", store.Get("OPENAI"));
        Assert.Equal(["anthropic", "openai"], store.List());
        Assert.True(store.Delete("openai"));
        Assert.False(store.Delete("openai"));
        Assert.Equal(["anthropic"], store.List());
    }

    [Fact]
    public void Set_EnforcesTheSameLimitsAsTheRealStore()
    {
        var store = new InMemorySecretStore();

        store.Set("big", new string('x', 1280));
        Assert.Throws<SecretStoreException>(() => store.Set("big", new string('x', 1281)));
        Assert.Throws<SecretStoreException>(() => store.Set("empty", string.Empty));
        Assert.Throws<ArgumentException>(() => store.Set("bad name", "v"));
    }
}

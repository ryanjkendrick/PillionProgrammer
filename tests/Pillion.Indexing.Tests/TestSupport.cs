using Microsoft.Data.Sqlite;
using Pillion.Common;
using Pillion.Common.Settings;
using Pillion.Indexing.Ownership;

// DPAPI and file ACLs are Windows-only, like Pillion Host.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace Pillion.Indexing.Tests;

/// <summary>A unique scratch directory that is deleted when disposed.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pillion-indexing-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string relative) => System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>Creates a file (and its folders) with text content and returns its full path.</summary>
    public string Write(string relative, string content = "content")
    {
        var full = File(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public string WriteBytes(string relative, byte[] content)
    {
        var full = File(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, content);
        return full;
    }

    public string MakeDirectory(string relative)
    {
        var full = File(relative);
        Directory.CreateDirectory(full);
        return full;
    }

    public PillionPaths NewPaths(string name = "data") => new(System.IO.Path.Combine(Path, name));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort cleanup of a temp folder.
        }
    }
}

/// <summary>Feature flags with every feature on unless switched off.</summary>
internal sealed class FakeFeatureFlags : IFeatureFlags
{
    private readonly HashSet<string> _off = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? Changed;

    public bool IsEnabled(string feature) => !_off.Contains(feature);

    public void Set(string feature, bool enabled)
    {
        if (enabled)
        {
            _off.Remove(feature);
        }
        else
        {
            _off.Add(feature);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>A process probe the test controls: which process ids are alive and who "we" are.</summary>
internal sealed class FakeProcessProbe : IProcessProbe
{
    private readonly Dictionary<int, DateTimeOffset> _alive = [];

    public FakeProcessProbe(int processId = 1000, DateTimeOffset? start = null)
    {
        CurrentProcessId = processId;
        CurrentProcessStart = start ?? new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);
        _alive[processId] = CurrentProcessStart;
    }

    public int CurrentProcessId { get; }

    public DateTimeOffset CurrentProcessStart { get; }

    public void SetAlive(int processId, DateTimeOffset start) => _alive[processId] = start;

    public void Kill(int processId) => _alive.Remove(processId);

    public bool IsAlive(int processId, DateTimeOffset startUtc) => _alive.TryGetValue(processId, out var start) && start == startUtc;
}

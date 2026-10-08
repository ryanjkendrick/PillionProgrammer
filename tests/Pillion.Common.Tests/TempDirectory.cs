namespace Pillion.Common.Tests;

/// <summary>A unique scratch directory that is deleted (including read-only files) when disposed.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pillion-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Gets a <see cref="PillionPaths"/> whose root is a (not yet created) child of this directory.</summary>
    public PillionPaths NewPaths(string name = "data") => new(System.IO.Path.Combine(Path, name));

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort cleanup of a temp folder.
        }
    }
}

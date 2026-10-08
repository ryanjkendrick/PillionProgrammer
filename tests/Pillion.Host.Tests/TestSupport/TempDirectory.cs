namespace Pillion.Host.Tests.TestSupport;

/// <summary>A unique folder under %TEMP%\pillion-tests, deleted on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pillion-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A log file may still be held briefly; leave it for the OS temp cleanup.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

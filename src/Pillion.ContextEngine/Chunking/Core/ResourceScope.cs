namespace Pillion.ContextEngine.Chunking.Core;

/// <summary>
/// Keeps native syntax trees alive until the assembler has finished with the units that point into them (split
/// points are computed lazily from tree nodes), then frees them.
/// </summary>
internal sealed class ResourceScope : IDisposable
{
    private readonly List<IDisposable> _resources = [];

    public T Add<T>(T resource)
        where T : IDisposable
    {
        _resources.Add(resource);
        return resource;
    }

    public void Dispose()
    {
        for (var i = _resources.Count - 1; i >= 0; i--)
        {
            _resources[i].Dispose();
        }

        _resources.Clear();
    }
}

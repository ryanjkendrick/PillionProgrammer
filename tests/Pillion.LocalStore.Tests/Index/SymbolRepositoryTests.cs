using Pillion.LocalStore.Index;

namespace Pillion.LocalStore.Tests.Index;

public sealed class SymbolRepositoryTests : IAsyncLifetime, IDisposable
{
    private readonly TempDirectory _temp = new();
    private IndexStorage _storage = null!;

    public async Task InitializeAsync() => _storage = await IndexTestSupport.OpenAsync(_temp);

    public async Task DisposeAsync()
    {
        await _storage.DisposeAsync();
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Symbols_AreStoredPerFile_AndFoundByNameIgnoringCase()
    {
        var file = await _storage.AddFileAsync("src/Orders.cs");
        var ids = await _storage.Symbols.ReplaceFileSymbolsAsync(file, [
            new NewSymbol("OrderService", "class", "Shop", 3, 40, "public sealed class OrderService"),
            new NewSymbol("GetOrderTotal", "method", "Shop.OrderService", 10, 14, "public decimal GetOrderTotal(Order order)"),
        ]);

        Assert.Equal(2, ids.Count);
        Assert.Equal(new SymbolRecord(ids[1], file, "GetOrderTotal", "method", "Shop.OrderService", 10, 14, "public decimal GetOrderTotal(Order order)"), await _storage.Symbols.GetSymbolAsync(ids[1]));
        Assert.Equal(ids, (await _storage.Symbols.GetSymbolsByFileAsync(file)).Select(s => s.Id));
        Assert.Equal(ids[0], Assert.Single(await _storage.Symbols.FindSymbolsByNameAsync("orderservice")).Id);
        Assert.Equal(2L, await _storage.Symbols.CountSymbolsAsync());
    }

    [Fact]
    public async Task References_AreStoredPerFile_AndQueriedByTarget()
    {
        var orders = await _storage.AddFileAsync("src/Orders.cs");
        var api = await _storage.AddFileAsync("src/Api.cs");
        var target = (await _storage.Symbols.ReplaceFileSymbolsAsync(orders, [new NewSymbol("GetOrderTotal", "method", null, 10, 14, null)]))[0];
        var caller = (await _storage.Symbols.ReplaceFileSymbolsAsync(api, [new NewSymbol("Checkout", "method", null, 5, 20, null)]))[0];

        await _storage.Symbols.ReplaceFileReferencesAsync(api, [
            new SymbolReference(caller, target, 0, 12, "call"),
            new SymbolReference(null, target, 0, 1, "import"),
        ]);

        var refs = await _storage.Symbols.GetReferencesToAsync(target);
        Assert.Equal(2, refs.Count);
        Assert.All(refs, r => Assert.Equal(api, r.FileId));
        Assert.Equal([1, 12], (await _storage.Symbols.GetReferencesInFileAsync(api)).Select(r => r.Line));
        Assert.Equal(2, (await _storage.Symbols.GetAllReferencesAsync()).Count);

        await _storage.Symbols.ReplaceFileReferencesAsync(api, [new SymbolReference(caller, target, 0, 13, "call")]);
        Assert.Equal(13, Assert.Single(await _storage.Symbols.GetReferencesInFileAsync(api)).Line);
    }

    [Fact]
    public async Task ReplacingAFilesSymbols_CascadesToReferencesFromAndToThem()
    {
        var orders = await _storage.AddFileAsync("src/Orders.cs");
        var api = await _storage.AddFileAsync("src/Api.cs");
        var target = (await _storage.Symbols.ReplaceFileSymbolsAsync(orders, [new NewSymbol("GetOrderTotal", "method", null, 10, 14, null)]))[0];
        var caller = (await _storage.Symbols.ReplaceFileSymbolsAsync(api, [new NewSymbol("Checkout", "method", null, 5, 20, null)]))[0];
        await _storage.Symbols.ReplaceFileReferencesAsync(api, [new SymbolReference(caller, target, 0, 12, "call")]);

        var newIds = await _storage.Symbols.ReplaceFileSymbolsAsync(orders, [new NewSymbol("GetOrderTotal", "method", null, 11, 15, null)]);

        Assert.True(newIds[0] > target);
        Assert.Empty(await _storage.Symbols.GetAllReferencesAsync());
    }

    [Fact]
    public async Task DeletingTheFileRow_RemovesItsSymbols()
    {
        var file = await _storage.AddFileAsync("src/Orders.cs");
        await _storage.Symbols.ReplaceFileSymbolsAsync(file, [new NewSymbol("A", "class", null, 1, 2, null)]);

        await _storage.ExecuteAsync($"DELETE FROM files WHERE id = {file}");

        Assert.Equal(0L, await _storage.Symbols.CountSymbolsAsync());
    }
}

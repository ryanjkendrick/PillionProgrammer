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
    public async Task ReplacingAFilesSymbolsWithoutStableIds_CascadesToReferencesFromAndToThem()
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

    [Fact]
    public async Task StableIds_AreUpsertedInPlace_SoReferencesFromOtherFilesSurviveAndVanishedSymbolsCascade()
    {
        const long Total = 4_611_686_018_427_387_904; // stable ids are 63-bit hashes
        const long Ship = 1_234_567_890_123;
        var orders = await _storage.AddFileAsync("src/Orders.cs");
        var api = await _storage.AddFileAsync("src/Api.cs");
        await _storage.Symbols.ReplaceFileSymbolsAsync(orders, [
            new NewSymbol("GetOrderTotal", "method", "Orders", 10, 14, null) { Id = Total },
            new NewSymbol("Ship", "method", "Orders", 20, 24, null) { Id = Ship },
        ]);
        var caller = (await _storage.Symbols.ReplaceFileSymbolsAsync(api, [new NewSymbol("Checkout", "method", null, 5, 20, null) { Id = 77 }]))[0];
        await _storage.Symbols.ReplaceFileReferencesAsync(api, [
            new SymbolReference(caller, Total, 0, 12, "call"),
            new SymbolReference(caller, Ship, 0, 13, "call"),
        ]);

        // Orders.cs is edited: GetOrderTotal moves down (same id), Ship is removed, Pack is new.
        var ids = await _storage.Symbols.ReplaceFileSymbolsAsync(orders, [
            new NewSymbol("GetOrderTotal", "method", "Orders", 30, 34, "decimal GetOrderTotal()") { Id = Total },
            new NewSymbol("Pack", "method", "Orders", 40, 44, null),
        ]);

        Assert.Equal(Total, ids[0]);
        Assert.Equal(77, caller);
        Assert.Equal(new SymbolRecord(Total, orders, "GetOrderTotal", "method", "Orders", 30, 34, "decimal GetOrderTotal()"), await _storage.Symbols.GetSymbolAsync(Total));
        Assert.Null(await _storage.Symbols.GetSymbolAsync(Ship));
        Assert.Equal(["GetOrderTotal", "Pack"], (await _storage.Symbols.GetSymbolsByFileAsync(orders)).Select(s => s.Name));
        var survivors = await _storage.Symbols.GetAllReferencesAsync();
        Assert.Equal(Total, Assert.Single(survivors).ToSymbolId);
    }

    [Fact]
    public async Task References_KeepTheirAmbiguity_AndAFileLevelSource()
    {
        var file = await _storage.AddFileAsync("src/Orders.cs");
        var ids = await _storage.Symbols.ReplaceFileSymbolsAsync(file, [
            new NewSymbol("Save", "method", "A", 1, 2, null) { Id = 10 },
            new NewSymbol("Save", "method", "B", 3, 4, null) { Id = 11 },
        ]);

        await _storage.Symbols.ReplaceFileReferencesAsync(file, [
            new SymbolReference(null, ids[0], 0, 9, "call", Ambiguous: true, Candidates: 2),
            new SymbolReference(null, ids[1], 0, 9, "call", Ambiguous: true, Candidates: 2),
            new SymbolReference(ids[0], ids[1], 0, 2, "call"),
        ]);

        var refs = await _storage.Symbols.GetReferencesInFileAsync(file);
        Assert.Equal(new SymbolReference(ids[0], ids[1], file, 2, "call", false, 1), refs[0]);
        Assert.All(refs.Skip(1), r => Assert.Equal((null, true, 2), (r.FromSymbolId, r.Ambiguous, r.Candidates)));
    }

    [Fact]
    public async Task AStableIdTwiceInOneFile_IsRejected()
    {
        var file = await _storage.AddFileAsync("src/Orders.cs");

        await Assert.ThrowsAsync<ArgumentException>(() => _storage.Symbols.ReplaceFileSymbolsAsync(file, [
            new NewSymbol("A", "method", null, 1, 2, null) { Id = 5 },
            new NewSymbol("B", "method", null, 3, 4, null) { Id = 5 },
        ]));
        Assert.Equal(0L, await _storage.Symbols.CountSymbolsAsync());
    }

    [Fact]
    public async Task TheSymbolsVersion_IsStoredSeparatelyFromTheChunkerVersion()
    {
        Assert.Null(await _storage.Symbols.GetSymbolsVersionAsync());

        await _storage.Symbols.SetSymbolsVersionAsync("1");
        await _storage.Symbols.SetSymbolsVersionAsync("2");

        Assert.Equal("2", await _storage.Symbols.GetSymbolsVersionAsync());
        Assert.Equal(1L, await _storage.CountAsync("SELECT count(*) FROM meta WHERE key = 'symbols_version'"));
        Assert.Equal(0L, await _storage.CountAsync("SELECT count(*) FROM meta WHERE key = 'chunker_version'"));
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.Symbols.SetSymbolsVersionAsync(" "));
    }
}
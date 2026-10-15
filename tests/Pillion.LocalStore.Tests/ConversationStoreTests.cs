using Microsoft.Extensions.DependencyInjection;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.LocalStore.Conversations;

namespace Pillion.LocalStore.Tests;

public sealed class ConversationStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly TempDirectory _temp = new();
    private readonly FakeFeatureFlags _features = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task WithTheFlagOff_NothingIsCreated_AndTheCallIsRefusedNamingTheFlag()
    {
        var paths = _temp.NewPaths();
        _features.Set(FeatureNames.LocalStore, enabled: false);
        await using var store = new ConversationStore(paths, _features);

        var error = await Assert.ThrowsAsync<LocalStoreException>(() => store.GetRepositoryAsync());

        Assert.Equal(LocalStoreErrorKind.FeatureDisabled, error.Kind);
        Assert.Equal(PillionErrorCode.PolicyDenied, error.Error.Code);
        Assert.Contains("localStore", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(paths.Root));
    }

    [Fact]
    public async Task TheStore_CreatesConversationsDbAndItsKeyInTheDataRoot_AndHonoursTheFlagOnEveryCall()
    {
        var paths = _temp.NewPaths();
        await using var store = new ConversationStore(paths, _features);

        var repository = await store.GetRepositoryAsync();
        await repository.SaveConversationAsync(new ConversationRecord("c", "C", T0, T0));

        Assert.True(File.Exists(paths.ConversationsDatabase));
        Assert.Equal(Path.Combine(paths.Root, "conversations.db"), store.Database!.DatabasePath);
        Assert.Equal(Path.Combine(paths.Root, "conversations.key.bin"), store.Database.KeyFilePath);
        Assert.True(File.Exists(paths.ConversationsKeyFile));
        Assert.Same(repository, await store.GetRepositoryAsync());

        _features.Set(FeatureNames.LocalStore, enabled: false);
        await Assert.ThrowsAsync<LocalStoreException>(() => store.GetRepositoryAsync());
    }

    [Fact]
    public async Task AFailedOpen_IsRetriedOnTheNextCall()
    {
        var paths = _temp.NewPaths();
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.ConversationsDatabase, "not a database, and no key");
        await using var store = new ConversationStore(paths, _features);

        var error = await Assert.ThrowsAsync<LocalStoreException>(() => store.GetRepositoryAsync());
        Assert.Equal(LocalStoreErrorKind.KeyMissing, error.Kind);

        File.Delete(paths.ConversationsDatabase);
        var repository = await store.GetRepositoryAsync();
        Assert.Empty(await repository.ListConversationsAsync());
    }

    [Fact]
    public async Task AddPillionLocalStore_RegistersTheStoreAndRetention_AndTheContainerClosesTheDatabase()
    {
        var paths = _temp.NewPaths();
        var services = new ServiceCollection()
            .AddSingleton(paths)
            .AddSingleton<IFeatureFlags>(_features)
            .AddPillionLocalStore();

        var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IConversationStore>();
        Assert.IsType<ConversationStore>(store);
        Assert.NotNull(provider.GetRequiredService<ConversationRetention>());
        var repository = await store.GetRepositoryAsync();
        await repository.SaveConversationAsync(new ConversationRecord("c", "C", T0, T0));

        await provider.DisposeAsync();

        File.Move(paths.ConversationsDatabase, paths.ConversationsDatabase + ".moved"); // Closed: nothing holds it open.
    }
}

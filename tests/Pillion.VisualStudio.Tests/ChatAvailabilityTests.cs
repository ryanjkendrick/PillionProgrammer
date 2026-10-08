using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Tests;

#pragma warning disable VSTHRD003

/// <summary>The chat feature flag: it enables the Open Chat command and decides what the chat window shows.</summary>
public sealed class ChatAvailabilityTests : IDisposable
{
    private static readonly string AllOn =
        "{\"chat\":true,\"modelGateway\":true,\"connectivityProbes\":true,\"cloudProviders\":true,\"ollamaProvider\":true,\"openAICompatibleProvider\":true}";

    private static readonly string ChatOff =
        "{\"chat\":false,\"modelGateway\":true,\"connectivityProbes\":true,\"cloudProviders\":true,\"ollamaProvider\":true,\"openAICompatibleProvider\":true}";

    private readonly JoinableTaskContext _context = Task.Run(() => new JoinableTaskContext()).Result;
    private readonly InMemorySettingsService _settings = TestModels.Settings();
    private readonly ScriptedModelService _models = new();
    private readonly FakeHostConnection _connection;

    public ChatAvailabilityTests()
    {
        _connection = new FakeHostConnection().With<ISettingsService>(_settings).With<IModelService>(_models);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public void ChatIsAvailable_UntilTheHostSaysOtherwise()
    {
        using var availability = new ChatAvailability(_connection);

        Assert.True(availability.IsEnabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"modelGateway\":false}")]
    [InlineData("{\"chat\":\"no\"}")]
    [InlineData("{\"chat\":null}")]
    public async Task AnAnswerWithoutAChatFalse_LeavesChatOn(string? json)
    {
        using var availability = new ChatAvailability(_connection);
        if (json is not null)
        {
            _settings.Values[ChatAvailability.EffectiveFeaturesKey] = json;
        }

        await availability.RefreshAsync();

        Assert.True(availability.IsEnabled);
    }

    [Fact]
    public async Task ChatFalse_DisablesChat_AndRaisesChangedOnlyWhenItChanges()
    {
        using var availability = new ChatAvailability(_connection);
        var changes = 0;
        availability.Changed += (_, _) => Interlocked.Increment(ref changes);

        _settings.Values[ChatAvailability.EffectiveFeaturesKey] = AllOn;
        await availability.RefreshAsync();
        Assert.True(availability.IsEnabled);
        Assert.Equal(0, changes);

        _settings.Values[ChatAvailability.EffectiveFeaturesKey] = ChatOff;
        await availability.RefreshAsync();
        await availability.RefreshAsync();
        Assert.False(availability.IsEnabled);
        Assert.Equal(1, changes);

        _settings.Values[ChatAvailability.EffectiveFeaturesKey] = AllOn;
        await availability.RefreshAsync();
        Assert.True(availability.IsEnabled);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task WhenTheHostIsNotConnected_TheLastAnswerIsKept()
    {
        using var availability = new ChatAvailability(_connection);
        _settings.Values[ChatAvailability.EffectiveFeaturesKey] = ChatOff;
        await availability.RefreshAsync();
        Assert.False(availability.IsEnabled);

        await _connection.SetStateAsync(HostState.Restarting, attempt: 1);
        await availability.RefreshAsync(); // GetProxy throws HostRestarting; nothing changes and nothing is thrown.

        Assert.False(availability.IsEnabled);
    }

    [Fact]
    public async Task AMalformedAnswer_KeepsTheLastAnswer()
    {
        using var availability = new ChatAvailability(_connection);
        _settings.Values[ChatAvailability.EffectiveFeaturesKey] = ChatOff;
        await availability.RefreshAsync();

        _settings.Values[ChatAvailability.EffectiveFeaturesKey] = "{ not json";
        await availability.RefreshAsync();

        Assert.False(availability.IsEnabled);
    }

    [Fact]
    public async Task WhenTheHostBecomesReady_TheFlagsAreReadAgain()
    {
        var connection = new FakeHostConnection(HostState.Starting).With<ISettingsService>(_settings);
        _settings.Values[ChatAvailability.EffectiveFeaturesKey] = ChatOff;
        using var availability = new ChatAvailability(connection);
        availability.Start();
        Assert.True(availability.IsEnabled);

        await connection.SetStateAsync(HostState.Ready);

        await Wait.UntilAsync(() => !availability.IsEnabled, "chat disabled once the host is ready");

        // A restart re-reads them: the policy was lifted meanwhile.
        await connection.SetStateAsync(HostState.Restarting, attempt: 1);
        _settings.Values[ChatAvailability.EffectiveFeaturesKey] = AllOn;
        await connection.SetStateAsync(HostState.Ready);
        await Wait.UntilAsync(() => availability.IsEnabled, "chat enabled again after the restart");
    }

    [Fact]
    public async Task Start_ReadsTheFlagsAtOnce_WhenTheHostIsAlreadyReady()
    {
        _settings.Values[ChatAvailability.EffectiveFeaturesKey] = ChatOff;
        using var availability = new ChatAvailability(_connection);

        availability.Start();

        await Wait.UntilAsync(() => !availability.IsEnabled, "chat disabled");
    }

    [Fact]
    public async Task ADisposedAvailability_StopsListening()
    {
        var connection = new FakeHostConnection(HostState.Starting).With<ISettingsService>(_settings);
        _settings.Values[ChatAvailability.EffectiveFeaturesKey] = ChatOff;
        var availability = new ChatAvailability(connection);
        availability.Start();
        availability.Dispose();

        await connection.SetStateAsync(HostState.Ready);
        await Task.Delay(100);

        Assert.True(availability.IsEnabled);
    }

    [Fact]
    public async Task TheChatWindow_ShowsTheDisabledMessage_AndCannotSend()
    {
        _settings.Values[ChatAvailability.EffectiveFeaturesKey] = ChatOff;
        using var availability = new ChatAvailability(_connection);
        using var vm = new ChatViewModel(_connection, _context.Factory, new ChatViewModelOptions { FlushInterval = TimeSpan.Zero, Availability = availability });

        vm.Start();
        await Wait.UntilAsync(() => vm.IsChatDisabled, "the window shows that chat is disabled");
        await Wait.UntilAsync(() => vm.ModelPicker.SelectedModel is not null, "the models are listed");
        vm.InputText = "Hello";

        Assert.False(vm.IsChatEnabled);
        Assert.Equal("Chat is disabled by settings or policy", vm.ChatDisabledMessage);
        Assert.Equal(ChatAvailability.DisabledMessage, vm.ChatDisabledMessage);
        Assert.False(vm.SendCommand.CanExecute(null));
        await vm.SendAsync();
        Assert.Empty(vm.Turns);
    }

    [Fact]
    public async Task TheChatWindow_FollowsTheFlag_WhenItChanges_ThroughRefresh()
    {
        _settings.Values[ChatAvailability.EffectiveFeaturesKey] = ChatOff;
        using var availability = new ChatAvailability(_connection);
        using var vm = new ChatViewModel(_connection, _context.Factory, new ChatViewModelOptions { FlushInterval = TimeSpan.Zero, Availability = availability });
        vm.Start();
        await Wait.UntilAsync(() => vm.IsChatDisabled && vm.ModelPicker.SelectedModel is not null, "disabled and models listed");

        _settings.Values[ChatAvailability.EffectiveFeaturesKey] = AllOn;
        await vm.RefreshModelsAsync();
        await Wait.UntilAsync(() => vm.IsChatEnabled, "chat enabled again");
        vm.InputText = "Hello";

        Assert.Equal(string.Empty, vm.ChatDisabledMessage);
        Assert.True(vm.SendCommand.CanExecute(null));

        _settings.Values[ChatAvailability.EffectiveFeaturesKey] = ChatOff;
        await vm.RefreshModelsAsync();
        await Wait.UntilAsync(() => vm.IsChatDisabled, "chat disabled again");
        Assert.False(vm.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheChatWindow_IsUnaffected_WhenNoAvailabilityIsGiven()
    {
        using var vm = new ChatViewModel(_connection, _context.Factory, new ChatViewModelOptions { FlushInterval = TimeSpan.Zero });
        vm.Start();
        await Wait.UntilAsync(() => vm.ModelPicker.SelectedModel is not null, "the models are listed");
        vm.InputText = "Hello";

        Assert.True(vm.IsChatEnabled);
        Assert.False(vm.IsChatDisabled);
        Assert.Equal(string.Empty, vm.ChatDisabledMessage);
        Assert.True(vm.SendCommand.CanExecute(null));
    }
}

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;
using StreamJsonRpc;

namespace Pillion.VisualStudio.Tests;

// The tests await their own TaskCompletionSource tasks and the view models' tasks; no main thread is involved.
#pragma warning disable VSTHRD003

public sealed class ChatViewModelTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // Created on a thread-pool thread so the context has no SynchronizationContext: switching to the "main thread"
    // continues on the thread pool, which is what lets these tests run without a UI dispatcher.
    private readonly JoinableTaskContext _context = Task.Run(() => new JoinableTaskContext()).Result;
    private readonly ScriptedModelService _models = new();
    private readonly InMemorySettingsService _settings = TestModels.Settings();
    private readonly FakeHostConnection _connection;
    private int _openLogs;

    public ChatViewModelTests()
    {
        _connection = new FakeHostConnection().With<IModelService>(_models).With<ISettingsService>(_settings);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task Send_StreamsTheReplyAndTheHeaderNamesTheModelAndProvider()
    {
        using var vm = await StartAsync();
        var script = _models.Next();
        vm.InputText = "  What does this do?  ";

        var send = vm.SendAsync();
        var request = await script.Started.Task.WithTimeout(Timeout);
        script.Send("Hello ");
        script.Send("```csharp\nvar x = 1;\n```");
        script.Send(string.Empty, isFinal: true);
        await send.WithTimeout(Timeout);

        Assert.Equal(2, vm.Turns.Count);
        Assert.Equal("What does this do?", vm.Turns[0].Text);
        Assert.True(vm.Turns[0].IsUser);
        var reply = vm.Last();
        Assert.Equal(ChatTurnState.Complete, reply.State);
        Assert.Equal("Hello ```csharp\nvar x = 1;\n```", reply.Text);
        Assert.Equal("Qwen2.5 Coder 7B via Ollama, local", reply.Header);
        Assert.Equal(TestModels.Local.Id, reply.ModelId);
        Assert.False(vm.IsSending);
        Assert.Equal(string.Empty, vm.InputText);

        Assert.Equal(TestModels.Local.Id, request.ModelId);
        Assert.False(string.IsNullOrEmpty(request.RequestId));
        Assert.Equal(reply.RequestId, request.RequestId);
        var message = Assert.Single(request.Messages);
        Assert.Equal(ChatRole.User, message.Role);
        Assert.Equal("What does this do?", message.Content);
    }

    [Fact]
    public async Task Send_PassesTheConversationSoFarAndUsesANewRequestIdEachTime()
    {
        using var vm = await StartAsync();
        await ExchangeAsync(vm, "first", "one");
        var script = _models.Next();
        vm.InputText = "second";

        var send = vm.SendAsync();
        var request = await script.Started.Task.WithTimeout(Timeout);
        script.Finish();
        await send.WithTimeout(Timeout);

        Assert.Equal(["first", "one", "second"], request.Messages.Select(m => m.Content));
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.User], request.Messages.Select(m => m.Role));
        Assert.Equal(2, _models.Requests.Select(r => r.RequestId).Distinct().Count());
    }

    [Fact]
    public async Task Send_DiscardsChunksForAnotherRequestId()
    {
        using var vm = await StartAsync();
        var script = _models.Next();
        vm.InputText = "hi";

        var send = vm.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);
        script.Send("kept ");
        script.Send("STALE", requestId: "some-older-request");
        script.Send("also kept", isFinal: true);
        await send.WithTimeout(Timeout);

        Assert.Equal("kept also kept", vm.Last().Text);
    }

    [Fact]
    public async Task Send_ShowsChunksAsTheyArrive()
    {
        using var vm = await StartAsync();
        var script = _models.Next();
        vm.InputText = "hi";

        var send = vm.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);
        Assert.True(vm.IsSending);
        Assert.True(vm.Last().IsStreaming);
        script.Send("partial");
        await Wait.UntilAsync(() => vm.Last().Text == "partial", "the first chunk is shown");
        Assert.True(vm.StopCommand.CanExecute(null));
        Assert.False(vm.SendCommand.CanExecute(null));

        script.Send(" and the rest", isFinal: true);
        await send.WithTimeout(Timeout);
        Assert.Equal("partial and the rest", vm.Last().Text);
    }

    [Fact]
    public async Task Stop_CancelsOnTheHostWithTheRequestIdAndCancelsTheStream()
    {
        using var vm = await StartAsync();
        var script = _models.Next();
        vm.InputText = "hi";

        var send = vm.SendAsync();
        var request = await script.Started.Task.WithTimeout(Timeout);
        script.Send("partial");
        await Wait.UntilAsync(() => vm.Last().Text == "partial", "the first chunk is shown");

        await vm.StopAsync().WithTimeout(Timeout);
        await send.WithTimeout(Timeout);
        script.Send(" late chunk");

        var reply = vm.Last();
        Assert.Equal(ChatTurnState.Stopped, reply.State);
        Assert.Equal("Stopped.", reply.StateText);
        Assert.Equal("partial", reply.Text);
        Assert.False(reply.CanRetry);
        Assert.Equal([request.RequestId], _models.CancelledIds);
        Assert.True(await script.Cancelled.Task.WithTimeout(Timeout));
        Assert.False(vm.IsSending);
        Assert.False(vm.StopCommand.CanExecute(null));
    }

    [Fact]
    public async Task StoppedReply_IsKeptInTheHistoryOfTheNextRequest()
    {
        using var vm = await StartAsync();
        var script = _models.Next();
        vm.InputText = "hi";
        var send = vm.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);
        script.Send("partial");
        await Wait.UntilAsync(() => vm.Last().Text == "partial", "the first chunk is shown");
        await vm.StopAsync();
        await send.WithTimeout(Timeout);

        var next = _models.Next();
        vm.InputText = "go on";
        var second = vm.SendAsync();
        var request = await next.Started.Task.WithTimeout(Timeout);
        next.Finish();
        await second.WithTimeout(Timeout);

        Assert.Equal(["hi", "partial", "go on"], request.Messages.Select(m => m.Content));
    }

    [Fact]
    public async Task PillionError_IsShownInlineWithItsRetryHint_AndRetryStreamsAgain()
    {
        using var vm = await StartAsync();
        var script = _models.Next();
        vm.InputText = "hi";

        var send = vm.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);
        script.Fail(new PillionRpcException(new PillionError
        {
            Code = PillionErrorCode.ProviderUnavailable,
            Message = "Ollama is not running.",
            RetryHint = "Start Ollama, then retry.",
        }));
        await send.WithTimeout(Timeout);

        var failed = vm.Last();
        Assert.Equal(ChatTurnState.Failed, failed.State);
        Assert.Equal("Ollama is not running.", failed.ErrorMessage);
        Assert.Equal("Start Ollama, then retry.", failed.ErrorHint);
        Assert.True(failed.HasError);
        Assert.True(failed.CanRetry);
        Assert.Same(vm.RetryCommand, failed.RetryCommand);
        Assert.True(vm.RetryCommand.CanExecute(failed));

        var retry = _models.Next();
        var retried = vm.RetryAsync(failed);
        var request = await retry.Started.Task.WithTimeout(Timeout);
        retry.Send("Now it works", isFinal: true);
        await retried.WithTimeout(Timeout);

        Assert.Equal(2, vm.Turns.Count);
        Assert.DoesNotContain(failed, vm.Turns);
        Assert.Equal("Now it works", vm.Last().Text);
        Assert.Equal(ChatTurnState.Complete, vm.Last().State);
        Assert.Equal(["hi"], request.Messages.Select(m => m.Content));
    }

    [Fact]
    public async Task RemoteError_WithPillionErrorData_IsShownInline()
    {
        using var vm = await StartAsync();
        var script = _models.Next();
        vm.InputText = "hi";

        var send = vm.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);
        var error = new PillionError { Code = PillionErrorCode.PolicyDenied, Message = "Local-only mode is on.", RetryHint = "Pick a local model." };
        script.Fail(new RemoteInvocationException(error.Message, -32000, errorData: null, deserializedErrorData: error));
        await send.WithTimeout(Timeout);

        Assert.Equal(ChatTurnState.Failed, vm.Last().State);
        Assert.Equal("Local-only mode is on.", vm.Last().ErrorMessage);
        Assert.Equal("Pick a local model.", vm.Last().ErrorHint);
    }

    [Fact]
    public async Task HostRestart_MarksTheReplyInterrupted_ThenRetryWorksOnceReady()
    {
        using var vm = await StartAsync();
        var script = _models.Next();
        vm.InputText = "hi";

        var send = vm.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);
        script.Send("partial");
        await Wait.UntilAsync(() => vm.Last().Text == "partial", "the first chunk is shown");

        await _connection.SetStateAsync(HostState.Restarting, attempt: 1);
        await send.WithTimeout(Timeout);

        var interrupted = vm.Last();
        Assert.Equal(ChatTurnState.Interrupted, interrupted.State);
        Assert.Equal("partial", interrupted.Text);
        Assert.Contains("Interrupted", interrupted.StateText);
        Assert.True(interrupted.CanRetry);
        await Wait.UntilAsync(() => vm.HostState == HostState.Restarting, "the status line shows the restart");
        Assert.Equal("Pillion Host is restarting (1/3)...", vm.StatusText);
        Assert.False(vm.RetryCommand.CanExecute(interrupted));

        var listsBefore = Volatile.Read(ref _models.ListCalls);
        await _connection.SetStateAsync(HostState.Ready);
        await Wait.UntilAsync(() => vm.HostState == HostState.Ready && Volatile.Read(ref _models.ListCalls) > listsBefore, "models listed again after the restart");
        await Wait.UntilAsync(() => vm.RetryCommand.CanExecute(interrupted), "retry is offered");

        var retry = _models.Next();
        var retried = vm.RetryAsync(interrupted);
        await retry.Started.Task.WithTimeout(Timeout);
        retry.Send("complete answer", isFinal: true);
        await retried.WithTimeout(Timeout);

        Assert.Equal(2, vm.Turns.Count);
        Assert.Equal("complete answer", vm.Last().Text);
    }

    [Fact]
    public async Task ConnectionLost_DuringTheStream_IsAnInterruption()
    {
        using var vm = await StartAsync();
        var script = _models.Next();
        vm.InputText = "hi";

        var send = vm.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);
        script.Fail(new ConnectionLostException());
        await send.WithTimeout(Timeout);

        Assert.Equal(ChatTurnState.Interrupted, vm.Last().State);
        Assert.True(vm.Last().CanRetry);
        Assert.False(vm.Last().HasError);
    }

    [Fact]
    public async Task Clear_RemovesTheConversationAndStopsTheReply()
    {
        using var vm = await StartAsync();
        await ExchangeAsync(vm, "one", "two");
        var script = _models.Next();
        vm.InputText = "three";
        var send = vm.SendAsync();
        var request = await script.Started.Task.WithTimeout(Timeout);

        vm.ClearCommand.Execute(null);
        await send.WithTimeout(Timeout);

        Assert.Empty(vm.Turns);
        Assert.False(vm.IsSending);
        Assert.True(await script.Cancelled.Task.WithTimeout(Timeout));
        await Wait.UntilAsync(() => _models.CancelledIds.Contains(request.RequestId), "the host was asked to cancel");
        Assert.False(vm.ClearCommand.CanExecute(null));
    }

    [Fact]
    public async Task Send_IsUnavailableWithoutInputAModelOrAReadyHost()
    {
        using var vm = await StartAsync();
        Assert.False(vm.SendCommand.CanExecute(null));
        vm.InputText = "   ";
        Assert.False(vm.SendCommand.CanExecute(null));
        vm.InputText = "hi";
        Assert.True(vm.SendCommand.CanExecute(null));

        await _connection.SetStateAsync(HostState.Restarting);
        await Wait.UntilAsync(() => !vm.SendCommand.CanExecute(null), "send is disabled while restarting");
        Assert.False(vm.IsHostUsable);
        await vm.SendAsync();
        Assert.Empty(vm.Turns);
    }

    [Fact]
    public async Task StatusLine_FollowsTheHostState_AndOffersOpenLogsWhenFailed()
    {
        var connection = new FakeHostConnection(HostState.Starting).With<IModelService>(_models).With<ISettingsService>(_settings);
        using var vm = new ChatViewModel(connection, _context.Factory, new ChatViewModelOptions { OpenLogs = () => _openLogs++ });
        vm.Start();

        Assert.Equal("Starting Pillion Host...", vm.StatusText);
        Assert.Equal("Waiting for Pillion Host.", vm.ModelPicker.Message);

        await connection.SetStateAsync(HostState.Ready);
        await Wait.UntilAsync(() => vm.ModelPicker.Models.Count == 3, "Auto and the models listed once ready");
        Assert.Equal("Ready.", vm.StatusText);

        await connection.SetStateAsync(HostState.Failed, reason: "the host crashed 3 times in 5 minutes");
        await Wait.UntilAsync(() => vm.IsHostFailed, "failed");
        Assert.Equal("Pillion Host failed: the host crashed 3 times in 5 minutes", vm.StatusText);
        Assert.True(vm.OpenLogsCommand.CanExecute(null));
        vm.OpenLogsCommand.Execute(null);
        Assert.Equal(1, _openLogs);
    }

    [Fact]
    public async Task ListFailure_IsShownBesideThePicker()
    {
        _models.ListFailure = new PillionRpcException(new PillionError { Code = PillionErrorCode.ProviderUnavailable, Message = "No providers are configured." });
        using var vm = new ChatViewModel(_connection, _context.Factory);
        vm.Start();

        await Wait.UntilAsync(() => vm.ModelPicker.Message.StartsWith("Could not list models", StringComparison.Ordinal), "the failure is shown");
        Assert.Equal("Could not list models: No providers are configured.", vm.ModelPicker.Message);
        Assert.Null(vm.ModelPicker.SelectedModel);
    }

    [Fact]
    public void FormatStatus_CoversEveryState()
    {
        Assert.Equal("Starting Pillion Host...", ChatViewModel.FormatStatus(HostState.Starting, 0, 0, null));
        Assert.Equal("Ready.", ChatViewModel.FormatStatus(HostState.Ready, 0, 0, null));
        Assert.Equal("Ready; some providers are unavailable.", ChatViewModel.FormatStatus(HostState.Degraded, 0, 0, null));
        Assert.Equal("Pillion Host is restarting (2/3)...", ChatViewModel.FormatStatus(HostState.Restarting, 2, 3, null));
        Assert.Equal("Pillion Host is restarting...", ChatViewModel.FormatStatus(HostState.Restarting, 0, 3, null));
        Assert.Equal("Pillion Host failed.", ChatViewModel.FormatStatus(HostState.Failed, 0, 0, null));
    }

    [Fact]
    public async Task ReplyHeader_NamesTheModelTheChunksReport_NotThePick()
    {
        using var vm = await StartAsync();
        var script = _models.Next();
        vm.InputText = "Hi";

        var send = vm.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);
        Assert.Equal("Qwen2.5 Coder 7B via Ollama, local", vm.Last().Header);

        // The host fell back to another model: the header follows the chunks.
        script.SendFrom("openrouter", "gpt-test", "From the cloud.");
        await Wait.UntilAsync(() => vm.Last().Text.Length > 0, "the first chunk is shown");
        Assert.Equal("GPT Test via OpenRouter, cloud", vm.Last().Header);
        Assert.Equal("Reply from GPT Test via OpenRouter, cloud. Answering...", vm.Last().AutomationName);

        script.SendFrom("openrouter", "gpt-test", string.Empty, isFinal: true);
        await send.WithTimeout(Timeout);
        Assert.Equal("GPT Test via OpenRouter, cloud", vm.Last().Header);
        Assert.Equal(TestModels.Local.Id, vm.Last().ModelId); // Retry asks for the picked model again.
    }

    [Fact]
    public async Task ReplyHeader_NamesAnUnlistedModelByItsIds()
    {
        using var vm = await StartAsync();
        var script = _models.Next();
        vm.InputText = "Hi";

        var send = vm.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);
        script.SendFrom("ollama", "gemma4:e2b", "pong", isFinal: true);
        await send.WithTimeout(Timeout);

        Assert.Equal("gemma4:e2b via Ollama, local", vm.Last().Header);
    }

    [Fact]
    public async Task Auto_SendsNoModelId_AndTheHeaderNamesTheModelThatAnswered()
    {
        using var vm = await StartAsync();
        var auto = vm.ModelPicker.Models[0];
        Assert.True(auto.IsAuto);
        Assert.Equal(ModelOption.AutoGroup, auto.Group);
        Assert.True(auto.IsAvailable);
        vm.ModelPicker.SelectedModel = auto;
        var script = _models.Next();
        vm.InputText = "Choose for me";

        var send = vm.SendAsync();
        var request = await script.Started.Task.WithTimeout(Timeout);
        Assert.Null(request.ModelId);
        Assert.Equal(Profile.Auto, request.Profile);
        Assert.Equal("Auto (choosing a model)", vm.Last().Header);

        script.SendFrom("ollama", "qwen2.5-coder:7b", "Done.", isFinal: true);
        await send.WithTimeout(Timeout);
        Assert.Equal("Qwen2.5 Coder 7B via Ollama, local", vm.Last().Header);
        Assert.Equal(ModelOption.AutoId, vm.Last().ModelId);
    }

    [Fact]
    public async Task Auto_IsKeptAcrossARefresh_AndUsedByRetry()
    {
        using var vm = await StartAsync();
        vm.ModelPicker.SelectedModel = vm.ModelPicker.Models.Single(m => m.IsAuto);
        await vm.RefreshModelsAsync().WithTimeout(Timeout);
        Assert.True(vm.ModelPicker.SelectedModel?.IsAuto);

        var failing = _models.Next();
        vm.InputText = "Hi";
        var send = vm.SendAsync();
        await failing.Started.Task.WithTimeout(Timeout);
        failing.Fail(new PillionRpcException(new PillionError { Code = PillionErrorCode.ProviderUnavailable, Message = "down" }));
        await send.WithTimeout(Timeout);

        var retry = _models.Next();
        var retried = vm.RetryAsync(vm.Last());
        var request = await retry.Started.Task.WithTimeout(Timeout);
        Assert.Null(request.ModelId);
        retry.Send("ok", isFinal: true);
        await retried.WithTimeout(Timeout);
    }

    [Fact]
    public async Task AnErrorOnTheFinalChunk_KeepsTheTextAndShowsTheErrorInline()
    {
        using var vm = await StartAsync();
        var script = _models.Next();
        vm.InputText = "Explain";

        var send = vm.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);
        script.SendFrom("ollama", "qwen2.5-coder:7b", "It starts by ");
        script.SendFrom(
            "ollama",
            "qwen2.5-coder:7b",
            "reading",
            isFinal: true,
            error: new PillionError { Code = PillionErrorCode.ProviderUnavailable, Message = "Ollama stopped responding.", RetryHint = "Check that Ollama is running." });
        await send.WithTimeout(Timeout);

        var reply = vm.Last();
        Assert.Equal(ChatTurnState.Failed, reply.State);
        Assert.Equal("It starts by reading", reply.Text);
        Assert.Equal("Ollama stopped responding.", reply.ErrorMessage);
        Assert.Equal("Check that Ollama is running.", reply.ErrorHint);
        Assert.True(reply.CanRetry);
        Assert.False(vm.IsSending);
    }

    [Fact]
    public async Task StatusLine_ShowsConnectivity_AndAChangeListsTheModelsAgain()
    {
        using var vm = await StartAsync();
        Assert.Null(vm.Connectivity);
        Assert.Equal("Ready.", vm.StatusText);

        await _connection.SetConnectivityAsync(ConnectivityState.Online);
        await Wait.UntilAsync(() => vm.Connectivity == ConnectivityState.Online, "online shown");
        Assert.Equal("Ready. Online.", vm.StatusText);
        var listed = Volatile.Read(ref _models.ListCalls);

        _settings.Values["privacy.localOnly"] = "true";
        await _connection.SetConnectivityAsync(ConnectivityState.LocalOnly);
        await Wait.UntilAsync(() => vm.StatusText == "Ready. Local-only: nothing leaves this machine.", "local-only shown");
        await Wait.UntilAsync(() => Volatile.Read(ref _models.ListCalls) > listed, "the models are listed again");
        await Wait.UntilAsync(
            () => vm.ModelPicker.Models.SingleOrDefault(m => m.Id == TestModels.Cloud.Id)?.UnavailableReason == ChatModelPolicy.LocalOnlyReason,
            "the cloud model is greyed out");

        await _connection.SetConnectivityAsync(ConnectivityState.Offline);
        await Wait.UntilAsync(() => vm.StatusText == "Ready. Offline: cloud providers cannot be reached; local models still work.", "offline shown");

        await _connection.SetStateAsync(HostState.Restarting, attempt: 1);
        await Wait.UntilAsync(() => vm.HostState == HostState.Restarting, "restarting");
        Assert.Equal("Pillion Host is restarting (1/3)...", vm.StatusText); // No connectivity while the host is not usable.
    }

    [Fact]
    public void FormatStatus_AddsConnectivityOnlyWhileTheHostIsUsable()
    {
        Assert.Equal("Ready. Online.", ChatViewModel.FormatStatus(HostState.Ready, 0, 0, null, ConnectivityState.Online));
        Assert.Equal("Ready. Degraded: some cloud providers cannot be reached.", ChatViewModel.FormatStatus(HostState.Ready, 0, 0, null, ConnectivityState.Degraded));
        Assert.Equal("Ready. Offline: cloud providers cannot be reached; local models still work.", ChatViewModel.FormatStatus(HostState.Ready, 0, 0, null, ConnectivityState.Offline));
        Assert.Equal("Ready. Local-only: nothing leaves this machine.", ChatViewModel.FormatStatus(HostState.Ready, 0, 0, null, ConnectivityState.LocalOnly));
        Assert.Equal("Ready.", ChatViewModel.FormatStatus(HostState.Ready, 0, 0, null, null));
        Assert.Equal("Starting Pillion Host...", ChatViewModel.FormatStatus(HostState.Starting, 0, 0, null, ConnectivityState.Online));
    }

    private async Task<ChatViewModel> StartAsync()
    {
        var vm = new ChatViewModel(_connection, _context.Factory, new ChatViewModelOptions { FlushInterval = TimeSpan.Zero, OpenLogs = () => _openLogs++ });
        vm.Start();
        await Wait.UntilAsync(() => vm.ModelPicker.SelectedModel is not null, "the models are listed");
        return vm;
    }

    private async Task ExchangeAsync(ChatViewModel vm, string question, string answer)
    {
        var script = _models.Next();
        vm.InputText = question;
        var send = vm.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);
        script.Send(answer, isFinal: true);
        await send.WithTimeout(Timeout);
    }
}

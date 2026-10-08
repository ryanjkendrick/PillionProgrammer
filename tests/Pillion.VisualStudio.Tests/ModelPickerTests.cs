using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Tests;

// The tests await their own TaskCompletionSource tasks and the view models' tasks; no main thread is involved.
#pragma warning disable VSTHRD003

public sealed class ModelPickerTests : IDisposable
{
    private static readonly ModelDescriptor SecondLocal = new()
    {
        Id = "llama3.2:3b",
        DisplayName = "Llama 3.2 3B",
        ProviderId = "ollama",
        IsLocal = true,
        Capabilities = ModelCapabilities.Chat,
    };

    private readonly JoinableTaskContext _context = Task.Run(() => new JoinableTaskContext()).Result;

    public void Dispose() => _context.Dispose();

    [Fact]
    public void Build_GroupsLocalBeforeCloud_SortsByName_AndLeavesOutModelsThatCannotChat()
    {
        var policy = new ChatModelPolicy(localOnly: false, cloudChat: true, [new ProviderSummary("ollama", "Ollama", true)]);

        var options = ModelOption.Build([TestModels.Cloud, TestModels.Local, TestModels.EmbeddingOnly, SecondLocal], policy);

        Assert.Equal(["Llama 3.2 3B", "Qwen2.5 Coder 7B", "GPT Test"], options.Select(o => o.DisplayName));
        Assert.Equal([ModelOption.LocalGroup, ModelOption.LocalGroup, ModelOption.CloudGroup], options.Select(o => o.Group));
        Assert.All(options, o => Assert.True(o.IsAvailable));
        Assert.Equal("Qwen2.5 Coder 7B (Ollama)", options[1].DisplayText);
        Assert.Equal("GPT Test (openrouter)", options[2].DisplayText); // No display name configured: the id is shown.
        Assert.Equal("GPT Test via openrouter, cloud", options[2].ReplyHeader);
    }

    [Fact]
    public void Build_GreysCloudModelsOutInLocalOnlyMode_WithTheReason()
    {
        var options = ModelOption.Build([TestModels.Local, TestModels.Cloud], new ChatModelPolicy(localOnly: true, cloudChat: true, providers: null));

        var cloud = options.Single(o => !o.IsLocal);
        Assert.False(cloud.IsAvailable);
        Assert.Equal(ChatModelPolicy.LocalOnlyReason, cloud.UnavailableReason);
        Assert.Contains("unavailable: Local-only mode is on", cloud.AutomationName);
        Assert.Contains(ChatModelPolicy.LocalOnlyReason, cloud.ToolTip);
        Assert.True(options.Single(o => o.IsLocal).IsAvailable);
    }

    [Fact]
    public void Build_GreysCloudModelsOutWhenTheChatCloudSwitchIsOff()
    {
        var options = ModelOption.Build([TestModels.Local, TestModels.Cloud], new ChatModelPolicy(localOnly: false, cloudChat: false, providers: null));

        Assert.Equal(ChatModelPolicy.CloudChatOffReason, options.Single(o => !o.IsLocal).UnavailableReason);
    }

    [Fact]
    public void Build_GreysOutModelsOfADisabledProvider()
    {
        var policy = new ChatModelPolicy(false, true, [new ProviderSummary("ollama", "Ollama", enabled: false)]);

        var local = ModelOption.Build([TestModels.Local], policy).Single();

        Assert.False(local.IsAvailable);
        Assert.Contains("The provider Ollama is disabled", local.UnavailableReason);
    }

    [Fact]
    public void Picker_SelectsTheFirstAvailableLocalModel_KeepsTheChoiceOnRefresh_AndRefusesUnavailableModels()
    {
        var picker = new ModelPickerViewModel();
        var policy = new ChatModelPolicy(localOnly: true, cloudChat: true, providers: null);

        picker.SetModels(ModelOption.Build([TestModels.Cloud, TestModels.Local, SecondLocal], policy));
        Assert.Equal(SecondLocal.Id, picker.SelectedModel?.Id);
        Assert.False(picker.HasMessage);

        picker.SelectedModel = picker.Models.Single(m => m.Id == TestModels.Local.Id);
        picker.SelectedModel = picker.Models.Single(m => m.Id == TestModels.Cloud.Id);
        Assert.Equal(TestModels.Local.Id, picker.SelectedModel?.Id);

        picker.SetModels(ModelOption.Build([TestModels.Cloud, TestModels.Local, SecondLocal], ChatModelPolicy.Default));
        Assert.Equal(TestModels.Local.Id, picker.SelectedModel?.Id);
    }

    [Fact]
    public void Picker_ExplainsAnEmptyOrUnusableList()
    {
        var picker = new ModelPickerViewModel();

        picker.SetModels([]);
        Assert.Null(picker.SelectedModel);
        Assert.Contains("lists no chat models", picker.Message);

        picker.SetModels(ModelOption.Build([TestModels.Cloud], new ChatModelPolicy(true, true, null)));
        Assert.Null(picker.SelectedModel);
        Assert.Contains("No model can be used right now", picker.Message);
    }

    [Fact]
    public async Task ChatWindow_ReadsLocalOnlyAndProviderNamesFromTheHostSettings()
    {
        var models = new ScriptedModelService();
        var connection = new FakeHostConnection()
            .With<IModelService>(models)
            .With<ISettingsService>(TestModels.Settings(localOnly: true));
        using var vm = new ChatViewModel(connection, _context.Factory);

        vm.Start();
        await Wait.UntilAsync(() => vm.ModelPicker.Models.Count == 2, "models listed");

        var cloud = vm.ModelPicker.Models.Single(m => !m.IsLocal);
        Assert.Equal("OpenRouter", cloud.ProviderName);
        Assert.Equal(ChatModelPolicy.LocalOnlyReason, cloud.UnavailableReason);
        Assert.Equal("Ollama", vm.ModelPicker.SelectedModel?.ProviderName);
    }

    [Fact]
    public async Task ChatWindow_ShowsModelsAsListed_WhenTheSettingsCannotBeRead()
    {
        var connection = new FakeHostConnection().With<IModelService>(new ScriptedModelService());
        using var vm = new ChatViewModel(connection, _context.Factory);

        vm.Start();
        await Wait.UntilAsync(() => vm.ModelPicker.Models.Count == 2, "models listed");

        Assert.All(vm.ModelPicker.Models, m => Assert.True(m.IsAvailable));
    }
}

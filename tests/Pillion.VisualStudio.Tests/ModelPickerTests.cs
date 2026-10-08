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
        ModelId = "llama3.2:3b",
        DisplayName = "Llama 3.2 3B",
        ProviderId = "ollama",
        IsLocal = true,
        Capabilities = ModelCapabilities.Chat,
        IsAvailable = true,
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
        await Wait.UntilAsync(() => vm.ModelPicker.Models.Count == 3, "Auto and the models listed");

        var cloud = vm.ModelPicker.Models.Single(m => !m.IsLocal && !m.IsAuto);
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
        await Wait.UntilAsync(() => vm.ModelPicker.Models.Count == 3, "Auto and the models listed");

        Assert.All(vm.ModelPicker.Models, m => Assert.True(m.IsAvailable));
    }

    [Fact]
    public void Build_GreysOutModelsTheHostListsAsUnavailable_WithTheDigestInTheTooltip()
    {
        var missing = new ModelDescriptor
        {
            Id = "ollama/gemma4:e2b",
            ModelId = "gemma4:e2b",
            ProviderId = "ollama",
            IsLocal = true,
            Capabilities = ModelCapabilities.Chat,
            IsAvailable = false,
            Digest = "sha256:7fbdbf8f5e45",
        };
        var unreachable = new ModelDescriptor { Id = "openrouter/gpt-test", ModelId = "gpt-test", ProviderId = "openrouter", Capabilities = ModelCapabilities.Chat, IsAvailable = false };

        var options = ModelOption.Build([missing, unreachable, TestModels.Local], ChatModelPolicy.Default);

        var local = options.Single(o => o.Id == missing.Id);
        Assert.False(local.IsAvailable);
        Assert.Equal(ChatModelPolicy.LocalUnavailableReason, local.UnavailableReason);
        Assert.Equal("gemma4:e2b", local.DisplayName); // No display name: the provider's model id.
        Assert.Equal("gemma4:e2b", local.ModelId);
        Assert.Equal("ollama/gemma4:e2b" + Environment.NewLine + "Digest: sha256:7fbdbf8f5e45" + Environment.NewLine + ChatModelPolicy.LocalUnavailableReason, local.ToolTip);
        Assert.Equal(ChatModelPolicy.CloudUnavailableReason, options.Single(o => o.Id == unreachable.Id).UnavailableReason);
        Assert.True(options.Single(o => o.Id == TestModels.Local.Id).IsAvailable);

        // The settings' reasons are more precise, so they win over the host's flag.
        var localOnly = ModelOption.Build([unreachable], new ChatModelPolicy(localOnly: true, cloudChat: true, providers: null)).Single();
        Assert.Equal(ChatModelPolicy.LocalOnlyReason, localOnly.UnavailableReason);
    }

    [Fact]
    public void Picker_PutsAutoFirst_AvailableWhenAnyModelIs()
    {
        var picker = new ModelPickerViewModel();

        picker.SetModels(ModelOption.Build([TestModels.Cloud, TestModels.Local], ChatModelPolicy.Default));

        var auto = picker.Models[0];
        Assert.True(auto.IsAuto);
        Assert.Equal(ModelOption.AutoId, auto.Id);
        Assert.Equal(ModelOption.AutoGroup, auto.Group);
        Assert.Equal("Auto (Pillion picks the model)", auto.DisplayText);
        Assert.True(auto.IsAvailable);
        Assert.Equal(TestModels.Local.Id, picker.SelectedModel?.Id); // The default pick is still the first local model.
        Assert.Equal(3, picker.Models.Count);

        picker.SelectedModel = auto;
        picker.SetModels(ModelOption.Build([TestModels.Cloud, TestModels.Local], ChatModelPolicy.Default));
        Assert.True(picker.SelectedModel?.IsAuto);
        Assert.Equal(1, picker.Models.Count(m => m.IsAuto));

        // Nothing usable: Auto is greyed out with a reason and nothing is picked.
        picker.SetModels(ModelOption.Build([TestModels.Cloud], new ChatModelPolicy(true, true, null)));
        Assert.False(picker.Models[0].IsAvailable);
        Assert.Equal(ModelOption.AutoUnavailableReason, picker.Models[0].UnavailableReason);
        Assert.Null(picker.SelectedModel);
    }

    [Fact]
    public void ReplyHeaderFor_UsesTheListedEntry_OrTheIds()
    {
        var options = ModelOption.Build([TestModels.Local, TestModels.Cloud], new ChatModelPolicy(false, true, [new ProviderSummary("ollama", "Ollama", true)]));

        Assert.Equal("Qwen2.5 Coder 7B via Ollama, local", ModelOption.ReplyHeaderFor(options, "ollama", "qwen2.5-coder:7b"));
        Assert.Equal("gemma4:e2b via Ollama, local", ModelOption.ReplyHeaderFor(options, "ollama", "gemma4:e2b"));
        Assert.Equal("m via elsewhere", ModelOption.ReplyHeaderFor(options, "elsewhere", "m"));
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Tests;

// The tests await the view models' tasks; no main thread is involved.
#pragma warning disable VSTHRD003

/// <summary>The Indexing Options page's view model (work item 2.15): load, validation, and a save that writes only what changed.</summary>
public sealed class IndexingOptionsTests : IDisposable
{
    private readonly JoinableTaskContext _context = Task.Run(() => new JoinableTaskContext()).Result;
    private readonly DottedSettingsService _settings = new();
    private readonly ScriptedContextService _host = new();
    private readonly FakeHostConnection _connection;

    public IndexingOptionsTests()
    {
        _connection = new FakeHostConnection().With<ISettingsService>(_settings).With<IContextService>(_host);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task MissingSettings_ShowTheHostDefaults_AndTheModelLineSaysNoIndexYet()
    {
        using var vm = Create();

        await vm.LoadAsync();

        Assert.Equal(OptionsLoadState.Loaded, vm.LoadState);
        Assert.False(vm.IsDirty);
        Assert.Equal(string.Empty, vm.ExcludeText);
        Assert.Equal("1536", vm.MaxMemoryText);
        Assert.True(vm.PauseOnBattery);
        Assert.Equal("30", vm.RetentionText);
        Assert.StartsWith("Active embedding model: chosen when the first index is built", vm.ModelText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_ShowsTheSavedValues_AndTheModelTheOpenIndexWasBuiltWith()
    {
        _settings.Set("indexing", JObject.Parse("{\"exclude\":[\"**/generated/**\",\"*.log\"],\"maxMemoryMb\":2048,\"pauseOnBattery\":false,\"retentionDays\":7}"));
        _host.Status = IndexData.Idle(IndexData.Root(IndexData.RootA));
        using var vm = Create();

        await vm.LoadAsync();

        Assert.Equal("**/generated/**" + Environment.NewLine + "*.log", vm.ExcludeText);
        Assert.Equal("2048", vm.MaxMemoryText);
        Assert.False(vm.PauseOnBattery);
        Assert.Equal("7", vm.RetentionText);
        Assert.Equal("Active embedding model: bge-small-en-v1.5", vm.ModelText);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task Save_WritesOnlyTheChangedKeys_EachUnderItsOwnDottedKey_AndNothingElse()
    {
        _settings.Set("indexing", JObject.Parse("{\"exclude\":[\"*.log\"],\"maxMemoryMb\":2048,\"pauseOnBattery\":true,\"retentionDays\":7,\"futureOption\":\"keep me\"}"));
        _settings.Set("privacy.localOnly", true);
        _settings.Set("cloudSwitches.chat", false);
        using var vm = Create();
        await vm.LoadAsync();

        vm.ExcludeText = "*.log\r\n\r\n  **/bin/**  \r\n*.log";
        vm.PauseOnBattery = false;
        Assert.True(vm.IsDirty);
        await vm.SaveAsync();

        Assert.Equal(["indexing.exclude", "indexing.pauseOnBattery"], _settings.Writes.Select(w => w.Key));
        Assert.Equal("[\"*.log\",\"**/bin/**\"]", _settings.Writes[0].Json);
        Assert.Equal("false", _settings.Writes[1].Json);
        Assert.Equal("Saved.", vm.StatusMessage);
        Assert.False(vm.IsDirty);
        var indexing = (JObject)_settings.Get("indexing")!;
        Assert.Equal(2048, (int)indexing["maxMemoryMb"]!);
        Assert.Equal(7, (int)indexing["retentionDays"]!);
        Assert.Equal("keep me", (string?)indexing["futureOption"]);
        Assert.True((bool)_settings.Get("privacy.localOnly")!);
        Assert.False((bool)_settings.Get("cloudSwitches.chat")!);
    }

    [Fact]
    public async Task Save_WithNothingChanged_WritesNothing_AndASecondSaveOnlyWritesWhatChangedSince()
    {
        using var vm = Create();
        await vm.LoadAsync();

        await vm.SaveAsync();
        Assert.Empty(_settings.Writes);

        vm.MaxMemoryText = "3072";
        await vm.SaveAsync();
        Assert.Equal(["indexing.maxMemoryMb"], _settings.Writes.Select(w => w.Key));
        Assert.Equal("3072", _settings.Writes[0].Json);

        vm.RetentionText = "0";
        await vm.SaveAsync();
        Assert.Equal(["indexing.maxMemoryMb", "indexing.retentionDays"], _settings.Writes.Select(w => w.Key));
        Assert.Equal("0", _settings.Writes[1].Json);
    }

    [Fact]
    public async Task ASaveNeverOverwritesAValueAnotherPageOrAnEditorChangedMeanwhile()
    {
        _settings.Set("indexing", JObject.Parse("{\"exclude\":[],\"maxMemoryMb\":1536,\"pauseOnBattery\":true,\"retentionDays\":30}"));
        using var vm = Create();
        await vm.LoadAsync();

        // Another page (or an edit of settings.json) changes the retention after this page loaded.
        _settings.Set("indexing.retentionDays", 7);
        vm.ExcludeText = "*.tmp";
        await vm.SaveAsync();

        var indexing = (JObject)_settings.Get("indexing")!;
        Assert.Equal(7, (int)indexing["retentionDays"]!);
        Assert.Equal("*.tmp", (string?)indexing["exclude"]![0]);
        Assert.Equal(["indexing.exclude"], _settings.Writes.Select(w => w.Key));
    }

    [Theory]
    [InlineData("255")]
    [InlineData("65537")]
    [InlineData("lots")]
    [InlineData("")]
    [InlineData("1.5")]
    public async Task AMemoryCeilingOutsideTheRange_IsNotSaved_AndSaysWhy(string text)
    {
        using var vm = Create();
        await vm.LoadAsync();

        vm.MaxMemoryText = text;
        await vm.SaveAsync();

        Assert.Empty(_settings.Writes);
        Assert.Equal("Maximum memory must be a whole number of megabytes from 256 to 65536.", vm.StatusMessage);
        Assert.True(vm.IsDirty);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("3651")]
    [InlineData("a month")]
    public async Task ARetentionOutsideTheRange_IsNotSaved_AndSaysWhy(string text)
    {
        using var vm = Create();
        await vm.LoadAsync();

        vm.RetentionText = text;
        await vm.SaveAsync();

        Assert.Empty(_settings.Writes);
        Assert.Equal("Retention must be a whole number of days from 0 to 3650.", vm.StatusMessage);
    }

    [Fact]
    public async Task ZeroRetention_TurnsTheAgeRuleOff_AndIsSaved()
    {
        using var vm = Create();
        await vm.LoadAsync();

        vm.RetentionText = "0";
        await vm.SaveAsync();

        Assert.Equal("0", Assert.Single(_settings.Writes).Json);
        Assert.Equal("indexing.retentionDays", _settings.Writes[0].Key);
    }

    [Fact]
    public async Task AFailedSave_IsShown_AndTheEditsStay()
    {
        _settings.SetFailure = new PillionRpcException(new PillionError { Code = PillionErrorCode.PolicyDenied, Message = "The settings file is read-only." });
        using var vm = Create();
        await vm.LoadAsync();

        vm.PauseOnBattery = false;
        await vm.SaveAsync();

        Assert.Equal("Could not save: The settings file is read-only.", vm.StatusMessage);
        Assert.True(vm.IsDirty);
        Assert.False(vm.PauseOnBattery);
    }

    [Fact]
    public async Task AnIndexingChangeFromElsewhere_ReloadsAPageWithoutEdits_AndKeepsAPageWithEdits()
    {
        using var vm = Create();
        await vm.LoadAsync();
        _settings.Set("indexing.maxMemoryMb", 4096);

        await _connection.RaiseSettingsChangedAsync("indexing");
        await Wait.UntilAsync(() => vm.MaxMemoryText == "4096", "the reloaded value");

        vm.RetentionText = "90";
        _settings.Set("indexing.maxMemoryMb", 8192);
        await _connection.RaiseSettingsChangedAsync("indexing");
        await Task.Delay(200);

        Assert.Equal("4096", vm.MaxMemoryText);
        Assert.Equal("90", vm.RetentionText);
    }

    [Fact]
    public async Task WhileTheHostIsNotReady_ThePageSaysSo_AndLoadsWhenItIs()
    {
        var connection = new FakeHostConnection(HostState.Starting).With<ISettingsService>(_settings).With<IContextService>(_host);
        using var vm = new IndexingOptionsViewModel(connection, _context.Factory);

        await vm.LoadAsync();
        Assert.Equal(OptionsLoadState.HostNotReady, vm.LoadState);
        Assert.StartsWith("Host not ready", vm.StatusMessage, StringComparison.Ordinal);

        await connection.SetStateAsync(HostState.Ready);
        await Wait.UntilAsync(() => vm.IsLoaded, "the loaded page");
    }

    [Theory]
    [InlineData("a\r\nb", new[] { "a", "b" })]
    [InlineData("  a  \n\n b\n", new[] { "a", "b" })]
    [InlineData("a\na\nb\na", new[] { "a", "b" })]
    [InlineData("", new string[0])]
    [InlineData(null, new string[0])]
    public void SplitPatterns_TrimsDropsBlankLinesAndRepeats(string? text, string[] expected) =>
        Assert.Equal(expected, IndexingOptionsViewModel.SplitPatterns(text));

    private IndexingOptionsViewModel Create() => new(_connection, _context.Factory);

    /// <summary>A settings service that keeps a JSON document and addresses it by dotted key, as the host does.</summary>
    private sealed class DottedSettingsService : ISettingsService
    {
        private readonly object _gate = new();
        private readonly JObject _root = [];

        public List<(string Key, string Json)> Writes { get; } = [];

        public Exception? SetFailure { get; set; }

        public JToken? Get(string key)
        {
            lock (_gate)
            {
                JToken? current = _root;
                foreach (var segment in key.Split('.'))
                {
                    current = (current as JObject)?[segment];
                }

                return current;
            }
        }

        public void Set(string key, JToken value)
        {
            lock (_gate)
            {
                var segments = key.Split('.');
                var current = _root;
                for (var i = 0; i < segments.Length - 1; i++)
                {
                    current = (JObject)(current[segments[i]] ??= new JObject());
                }

                current[segments[segments.Length - 1]] = value;
            }
        }

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Get(key)?.ToString(Newtonsoft.Json.Formatting.None));

        public Task SetAsync(string key, string json, CancellationToken cancellationToken)
        {
            if (SetFailure is { } failure)
            {
                return Task.FromException(failure);
            }

            lock (_gate)
            {
                Writes.Add((key, json));
            }

            Set(key, JToken.Parse(json));
            return Task.CompletedTask;
        }

        public Task<ProviderTestResult> TestProviderAsync(string providerId, CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderTestResult { ProviderId = providerId, IsSuccess = true, Message = "OK" });
    }
}

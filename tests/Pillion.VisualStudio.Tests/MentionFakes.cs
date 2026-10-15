using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Pillion.Contracts;
using Pillion.VisualStudio.Core.Mentions;

namespace Pillion.VisualStudio.Tests;

/// <summary>An <see cref="IContextService"/> that answers mentions with a function set by the test and records every request.</summary>
internal sealed class FakeMentionService : IContextService
{
    public ConcurrentQueue<MentionRequest> Requests { get; } = new();

    public Func<MentionRequest, MentionResolution> Resolve { get; set; } = request => Resolved(request, "Program.cs", @"C:\src\Program.cs", "class Program { }", 5);

    public static MentionResolution Resolved(MentionRequest request, string name, string? path, string content, int tokens, string kind = "file", bool sensitive = false) => new()
    {
        Mention = request.Mention,
        IsResolved = true,
        Kind = kind,
        DisplayName = name,
        FilePath = path,
        Content = content,
        TokenCount = tokens,
        IsSensitive = sensitive,
    };

    public Task<MentionResolution> ResolveMentionAsync(MentionRequest request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        return Task.FromResult(Resolve(request));
    }

    public Task<SearchResult[]> SearchAsync(SearchRequest request, CancellationToken cancellationToken) => Task.FromResult<SearchResult[]>([]);

    public Task<IndexStatus> GetIndexStatusAsync(CancellationToken cancellationToken) => Task.FromResult(new IndexStatus());

    public Task PauseIndexingAsync(bool pause, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<SearchResponse> SearchDetailedAsync(SearchRequest request, CancellationToken cancellationToken) => Task.FromResult(new SearchResponse());

    public Task<ExcludedFilesPage> ListExcludedFilesAsync(ExcludedFilesRequest request, CancellationToken cancellationToken) => Task.FromResult(new ExcludedFilesPage());

    public Task RebuildIndexAsync(string rootPath, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<bool> DeleteIndexAsync(string indexId, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<IndexInfo[]> ListIndexesAsync(CancellationToken cancellationToken) => Task.FromResult<IndexInfo[]>([]);

    public Task SetWorkspaceAsync(WorkspaceDescription workspace, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>An <see cref="IDiagnosticsService"/> whose preview and Outbound records are set by the test.</summary>
internal sealed class FakeDiagnostics : IDiagnosticsService
{
    public ConcurrentQueue<ChatRequest> Previews { get; } = new();

    public List<OutboundRecordDto> Records { get; } = [];

    public int OutboundReads;

    public Func<ChatRequest, ContextReportDto> Preview { get; set; } = request =>
    {
        var items = new List<ContextReportItemDto> { new() { Kind = "system", Name = "System prompt", Tokens = 30, OriginalTokens = 30 } };
        for (var i = 0; i < request.Pins.Length; i++)
        {
            items.Add(new ContextReportItemDto { Kind = "pin", Name = request.Pins[i].DisplayName, Tokens = request.Pins[i].TokenCount, OriginalTokens = request.Pins[i].TokenCount, PinIndex = i });
        }

        items.Add(new ContextReportItemDto { Kind = "query", Name = "Your message", Tokens = 4, OriginalTokens = 4 });
        return new ContextReportDto { ModelId = "qwen2.5-coder:7b", ProviderId = "ollama", IsLocal = true, ContextWindow = 8192, OutputReserve = 2048, BudgetTokens = 6114, TotalTokens = 50, Items = [.. items], IsAssembled = request.Pins.Length > 0 };
    };

    public Task<AutocompleteHostCounters> GetAutocompleteCountersAsync(CancellationToken cancellationToken) => Task.FromResult(new AutocompleteHostCounters());

    public Task<OutboundRecordDto[]> GetOutboundRecordsAsync(int maxCount, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref OutboundReads);
        lock (Records)
        {
            return Task.FromResult(Records.ToArray());
        }
    }

    public Task<ContextReportDto> PreviewContextAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        Previews.Enqueue(request);
        return Task.FromResult(Preview(request));
    }
}

/// <summary>A fixed solution file list.</summary>
internal sealed class FakeWorkspaceFiles(WorkspaceFileList list) : IWorkspaceFiles
{
    public event EventHandler? Changed;

    public WorkspaceFileList Current { get; private set; } = list;

    public int Requests;

    public void Replace(WorkspaceFileList list)
    {
        Current = list;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<WorkspaceFileList> GetAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Requests);
        return Task.FromResult(Current);
    }
}

/// <summary>An editor whose selection, clipboard, diagnostics and current file are set by the test.</summary>
internal sealed class FakeEditorContext : IChatEditorContext
{
    public string? Selection { get; set; }

    public string? Clipboard { get; set; }

    public string Errors { get; set; } = string.Empty;

    public CurrentFileContext? CurrentFile { get; set; }

    public int CurrentFileReads;

    public Task<string?> GetSelectionAsync(CancellationToken cancellationToken) => Task.FromResult(Selection);

    public Task<string?> GetClipboardTextAsync(CancellationToken cancellationToken) => Task.FromResult(Clipboard);

    public Task<string> GetErrorsPayloadAsync(CancellationToken cancellationToken) => Task.FromResult(Errors);

    public Task<CurrentFileContext?> GetCurrentFileAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref CurrentFileReads);
        return Task.FromResult(CurrentFile);
    }
}

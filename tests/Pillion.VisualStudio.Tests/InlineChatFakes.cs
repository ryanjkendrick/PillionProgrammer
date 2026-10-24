using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Pillion.Contracts;
using Pillion.VisualStudio.Core.InlineChat;

namespace Pillion.VisualStudio.Tests;

/// <summary>An inline edit stream driven step by step by the test.</summary>
#pragma warning disable CA1001 // Lives as long as the test; the semaphore holds no unmanaged state once unused.
internal sealed class InlineEditScript
{
    private static readonly object End = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly ConcurrentQueue<object> _items = new();

    public TaskCompletionSource<InlineEditRequest> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Gets a task that completes when the session stopped reading the stream before its end: its token was cancelled, or it
    /// disposed the stream early. The session closes on Esc by forgetting the request first (so it stops reading), then asks
    /// the host to cancel and only then cancels its token; which of the two the stream sees first depends on timing (the test
    /// that waited for the token alone failed whenever it ran first in its process).
    /// </summary>
    public TaskCompletionSource<bool> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Send(string text, bool reset = false) =>
        Push(new InlineEditChunk { Text = text, ResetText = reset, ModelId = "qwen2.5-coder:7b", ProviderId = "ollama" });

    public void Final(string text = "", string? explanation = null, string? warning = null, int rejectedBlocks = 0, PillionError? error = null) =>
        Push(new InlineEditChunk
        {
            Text = text,
            IsFinal = true,
            Explanation = explanation,
            Warning = warning,
            RejectedBlocks = rejectedBlocks,
            Error = error,
            ModelId = "qwen2.5-coder:7b",
            ProviderId = "ollama",
        });

    public void Push(InlineEditChunk chunk) => PushItem(chunk);

    public void Fail(Exception exception) => PushItem(exception);

    public void Finish() => PushItem(End);

    public async IAsyncEnumerable<InlineEditChunk> RunAsync(InlineEditRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Started.TrySetResult(request);
        var ended = false;
        try
        {
            while (true)
            {
                await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
                _items.TryDequeue(out var item);
                if (ReferenceEquals(item, End))
                {
                    ended = true;
                    yield break;
                }

                if (item is Exception exception)
                {
                    ended = true;
                    throw exception;
                }

                var chunk = (InlineEditChunk)item!;
                if (chunk.RequestId.Length == 0)
                {
                    chunk.RequestId = request.RequestId;
                }

                ended = chunk.IsFinal;
                yield return chunk;
            }
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested || !ended)
            {
                Cancelled.TrySetResult(true);
            }
        }
    }

    private void PushItem(object item)
    {
        _items.Enqueue(item);
        _signal.Release();
    }
}
#pragma warning restore CA1001

/// <summary>An <see cref="IInlineEditService"/> that plays <see cref="InlineEditScript"/>s in order.</summary>
internal sealed class ScriptedInlineEditService : IInlineEditService
{
    private readonly ConcurrentQueue<InlineEditScript> _scripts = new();

    public ConcurrentQueue<InlineEditRequest> Requests { get; } = new();

    public ConcurrentQueue<string> CancelledIds { get; } = new();

    public InlineEditScript Next()
    {
        var script = new InlineEditScript();
        _scripts.Enqueue(script);
        return script;
    }

    public IAsyncEnumerable<InlineEditChunk> EditAsync(InlineEditRequest request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        if (!_scripts.TryDequeue(out var script))
        {
            throw new InvalidOperationException("No inline edit script queued.");
        }

        return script.RunAsync(request, cancellationToken);
    }

    public Task CancelEditAsync(string requestId, CancellationToken cancellationToken)
    {
        CancelledIds.Enqueue(requestId);
        return Task.CompletedTask;
    }
}

/// <summary>An <see cref="IInlineChatEditor"/> that records what the session asked for.</summary>
internal sealed class RecordingInlineChatEditor : IInlineChatEditor
{
    public List<(string Expected, string Proposal)> Replaced { get; } = [];

    public List<string> Inserted { get; } = [];

    public List<string> Copied { get; } = [];

    public int Closed { get; private set; }

    /// <summary>Gets or sets the reason the next replace or insert is refused, or <see langword="null"/> to accept it.</summary>
    public string? RefuseWith { get; set; }

    public string? ReplaceScope(string expectedScopeText, string proposal)
    {
        if (RefuseWith is { } reason)
        {
            return reason;
        }

        Replaced.Add((expectedScopeText, proposal));
        return null;
    }

    public string? InsertAtCaret(string text)
    {
        if (RefuseWith is { } reason)
        {
            return reason;
        }

        Inserted.Add(text);
        return null;
    }

    public void Copy(string text) => Copied.Add(text);

    public void Close() => Closed++;
}

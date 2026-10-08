using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Tests;

/// <summary>An <see cref="IHostConnection"/> whose state and services are set by the test.</summary>
internal sealed class FakeHostConnection : IHostConnection
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, object> _services = [];
    private HostState _state;

    public FakeHostConnection(HostState state = HostState.Ready)
    {
        _state = state;
    }

    public event EventHandler<HostStateChangedEventArgs>? StateChanged;

    public HostState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public FakeHostConnection With<T>(T service)
        where T : class
    {
        lock (_gate)
        {
            _services[typeof(T)] = service;
        }

        return this;
    }

    /// <summary>Changes the state and raises <see cref="StateChanged"/> on a thread-pool thread, as the supervisor does.</summary>
    public Task SetStateAsync(HostState state, int attempt = 0, string? reason = null)
    {
        HostState previous;
        lock (_gate)
        {
            previous = _state;
            _state = state;
        }

        var args = new HostStateChangedEventArgs(previous, state, attempt, 3, reason);
        return Task.Run(() => StateChanged?.Invoke(this, args));
    }

    public T GetProxy<T>()
        where T : class
    {
        lock (_gate)
        {
            if (_state is not (HostState.Ready or HostState.Degraded) || !_services.TryGetValue(typeof(T), out var service))
            {
                throw new PillionRpcException(new PillionError { Code = PillionErrorCode.HostRestarting, Message = "Pillion Host is not connected." });
            }

            return (T)service;
        }
    }

    public Task WhenReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A chat stream driven step by step by the test.</summary>
#pragma warning disable CA1001 // Lives as long as the test; the semaphore holds no unmanaged state once unused.
internal sealed class ChatScript
{
    private static readonly object End = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly ConcurrentQueue<object> _items = new();

    public TaskCompletionSource<ChatRequest> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Send(string text, bool isFinal = false, string? requestId = null) =>
        Push(new ChatChunk { Text = text, IsFinal = isFinal, RequestId = requestId ?? string.Empty });

    public void Fail(Exception exception) => Push(exception);

    public void Finish() => Push(End);

    public async IAsyncEnumerable<ChatChunk> RunAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Recorded in finally rather than with token.Register: the semaphore's own registration runs first and can
        // finish the iterator (disposing a registration) before a later-registered callback would run.
        Started.TrySetResult(request);
        try
        {
            while (true)
            {
                await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
                _items.TryDequeue(out var item);
                if (ReferenceEquals(item, End))
                {
                    yield break;
                }

                if (item is Exception exception)
                {
                    throw exception;
                }

                var chunk = (ChatChunk)item!;
                if (chunk.RequestId.Length == 0)
                {
                    chunk.RequestId = request.RequestId;
                }

                yield return chunk;
            }
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult(true);
            }
        }
    }

    private void Push(object item)
    {
        _items.Enqueue(item);
        _signal.Release();
    }
}
#pragma warning restore CA1001

/// <summary>An <see cref="IModelService"/> that lists set models and plays <see cref="ChatScript"/>s in order.</summary>
internal sealed class ScriptedModelService : IModelService
{
    private readonly ConcurrentQueue<ChatScript> _scripts = new();

    public ModelDescriptor[] Models { get; set; } = [TestModels.Local, TestModels.Cloud];

    public Exception? ListFailure { get; set; }

    public ConcurrentQueue<ChatRequest> Requests { get; } = new();

    public ConcurrentQueue<string> CancelledIds { get; } = new();

    public int ListCalls;

    public ChatScript Next()
    {
        var script = new ChatScript();
        _scripts.Enqueue(script);
        return script;
    }

    public IAsyncEnumerable<ChatChunk> ChatAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        if (!_scripts.TryDequeue(out var script))
        {
            throw new InvalidOperationException("No chat script queued.");
        }

        return script.RunAsync(request, cancellationToken);
    }

    public Task CancelAsync(string requestId, CancellationToken cancellationToken)
    {
        CancelledIds.Enqueue(requestId);
        return Task.CompletedTask;
    }

    public Task<ModelDescriptor[]> ListModelsAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref ListCalls);
        return ListFailure is { } failure ? Task.FromException<ModelDescriptor[]>(failure) : Task.FromResult(Models);
    }

    public IAsyncEnumerable<CompletionChunk> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>An in-memory <see cref="ISettingsService"/> keyed by the exact key strings the view models use.</summary>
internal sealed class InMemorySettingsService : ISettingsService
{
    public ConcurrentDictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<(string Key, string Json)> Writes { get; } = new();

    public Exception? SetFailure { get; set; }

    public Func<string, ProviderTestResult>? Tester { get; set; }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(Values.TryGetValue(key, out var json) ? json : null);

    public Task SetAsync(string key, string json, CancellationToken cancellationToken)
    {
        if (SetFailure is { } failure)
        {
            return Task.FromException(failure);
        }

        Writes.Enqueue((key, json));
        Values[key] = json;
        return Task.CompletedTask;
    }

    public Task<ProviderTestResult> TestProviderAsync(string providerId, CancellationToken cancellationToken) =>
        Task.FromResult(Tester?.Invoke(providerId) ?? new ProviderTestResult { ProviderId = providerId, IsSuccess = true, Message = "OK" });
}

/// <summary>A credential store in memory, recording what was written (tests only).</summary>
internal sealed class InMemoryCredentialStore : ICredentialStore
{
    public ConcurrentDictionary<string, string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentQueue<string> Deleted { get; } = new();

    public bool Exists(string providerId) => Keys.ContainsKey(providerId);

    public void Write(string providerId, string secret) => Keys[providerId] = secret;

    public bool Delete(string providerId)
    {
        Deleted.Enqueue(providerId);
        return Keys.TryRemove(providerId, out _);
    }
}

internal static class TestModels
{
    public static ModelDescriptor Local { get; } = new()
    {
        Id = "qwen2.5-coder:7b",
        DisplayName = "Qwen2.5 Coder 7B",
        ProviderId = "ollama",
        IsLocal = true,
        Capabilities = ModelCapabilities.Chat | ModelCapabilities.Fim,
    };

    public static ModelDescriptor Cloud { get; } = new()
    {
        Id = "gpt-test",
        DisplayName = "GPT Test",
        ProviderId = "openrouter",
        IsLocal = false,
        Capabilities = ModelCapabilities.Chat,
    };

    public static ModelDescriptor EmbeddingOnly { get; } = new()
    {
        Id = "nomic-embed",
        DisplayName = "Nomic Embed",
        ProviderId = "ollama",
        IsLocal = true,
        Capabilities = ModelCapabilities.Embeddings,
    };

    public static string Providers { get; } =
        "[{\"id\":\"ollama\",\"kind\":\"ollama\",\"displayName\":\"Ollama\",\"endpoint\":\"http://localhost:11434\",\"models\":[\"qwen2.5-coder:7b\"],\"enabled\":true},"
        + "{\"id\":\"openrouter\",\"kind\":\"openai-compatible\",\"displayName\":\"OpenRouter\",\"endpoint\":\"https://openrouter.ai/api/v1\",\"models\":[\"gpt-test\"],\"enabled\":true,\"proxy\":{\"url\":\"http://proxy.example:8080\"}}]";

    public static InMemorySettingsService Settings(bool localOnly = false, bool cloudChat = true, string? providers = null)
    {
        var settings = new InMemorySettingsService();
        settings.Values["privacy.localOnly"] = localOnly ? "true" : "false";
        settings.Values["cloudSwitches.chat"] = cloudChat ? "true" : "false";
        settings.Values["providers"] = providers ?? Providers;
        return settings;
    }

    public static ChatTurnViewModel Last(this ChatViewModel vm) => vm.Turns.Last();
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common;
using Pillion.Common.Settings;
using Pillion.ContextEngine.Chunking;
using Pillion.Contracts;
using Pillion.Host.Connectivity;
using Pillion.Host.Context;
using Pillion.Host.Indexing;
using Pillion.Host.Tests.TestSupport;
using Pillion.Indexing.Ownership;
using Pillion.Indexing.Retention;
using Pillion.ModelGateway;

namespace Pillion.Host.Tests.Indexing;

/// <summary>A deterministic embedder: hashed bags of words, so related texts get similar vectors. Records every batch.</summary>
internal sealed class FakeEmbedder : IIndexEmbedder
{
    public const int Dimensions = 64;

    private readonly ConcurrentQueue<int> _batches = new();

    public string ModelId { get; init; } = "fake/hashed-words";

    /// <summary>Gets or sets a delay per batch (status tests slow the run down so several notifications are sent).</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>Gets or sets an error thrown by the next batches (counts down).</summary>
    public int FailNext { get; set; }

    public IReadOnlyList<int> BatchSizes => [.. _batches];

    public int ChunksEmbedded => _batches.Sum();

    /// <summary>Gets every text handed to the model, in the order embedded.</summary>
    public ConcurrentQueue<string> Texts { get; } = new();

    public static EmbeddedVector Vector(string text)
    {
        var raw = new float[Dimensions];
        foreach (var word in Words(text))
        {
            var hash = Stable(word);
            raw[hash % Dimensions] += 1;
            raw[(hash / Dimensions) % Dimensions] += 0.5f;
        }

        raw[0] += 0.01f; // never all zero
        return EmbeddingVectors.Process(raw, keepFloat32: false);
    }

    public int MemoryReleases { get; private set; }

    public void ReleaseMemory() => MemoryReleases++;

    public IndexEmbeddingModel ResolveModel() => new(ModelId, TokenCounters.ApproximateWordPiece, 512, "bert-wordpiece");

    public async Task<IReadOnlyList<EmbeddedVector>> EmbedAsync(IndexEmbeddingModel model, IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        if (FailNext > 0)
        {
            FailNext--;
            throw new PillionRpcException(new PillionError { Code = PillionErrorCode.ProviderUnavailable, Message = "The fake embedder failed on purpose." });
        }

        _batches.Enqueue(texts.Count);
        foreach (var text in texts)
        {
            Texts.Enqueue(text);
        }

        return [.. texts.Select(Vector)];
    }

    private static IEnumerable<string> Words(string text)
    {
        var word = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                word.Append(c);
            }
            else if (word.Length > 0)
            {
                yield return word.ToString();
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            yield return word.ToString();
        }
    }

    private static uint Stable(string word)
    {
        // FNV-1a over the lower-cased word: stable across processes.
        var hash = 2166136261u;
        foreach (var c in word.ToLowerInvariant())
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash;
    }
}

/// <summary>Records every notification the status notifier sends, with its time.</summary>
internal sealed class RecordingStatusSender : INotificationSender
{
    private readonly List<(DateTimeOffset At, IndexStatus Status)> _sent = [];

    public event EventHandler? ClientConnected;

    public List<(DateTimeOffset At, IndexStatus Status)> Sent
    {
        get
        {
            lock (_sent)
            {
                return [.. _sent];
            }
        }
    }

    public Task<bool> TrySendAsync(string method, object? argument)
    {
        lock (_sent)
        {
            _sent.Add((DateTimeOffset.UtcNow, (IndexStatus)argument!));
        }

        return Task.FromResult(true);
    }

    public void RaiseConnected() => ClientConnected?.Invoke(this, EventArgs.Empty);
}

internal sealed class AllFlags : IFeatureFlags
{
    public event EventHandler? Changed;

    public ConcurrentDictionary<string, bool> Disabled { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEnabled(string feature) => !Disabled.ContainsKey(feature);

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}

internal sealed class FakePower : IPowerStatus
{
    public volatile bool BatterySaver;

    public bool IsBatterySaverOn() => BatterySaver;
}

internal sealed class FakeMemory : IMemorySampler
{
    private long _bytes = 100L * 1024 * 1024;

    public long Bytes
    {
        get => Interlocked.Read(ref _bytes);
        set => Interlocked.Exchange(ref _bytes, value);
    }

    public long WorkingSetBytes() => Bytes;
}

/// <summary>One indexing service over a data folder, with fakes for everything outside the pipeline.</summary>
internal sealed class IndexingHarness : IAsyncDisposable
{
    public IndexingHarness(string dataRoot, IIndexEmbedder? embedder = null, IndexingOptions? options = null, PillionSettings? settings = null, string pipeName = "pillion-test-host", IProcessProbe? probe = null, IMemorySampler? memory = null, TimeProvider? time = null, Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
    {
        Paths = new PillionPaths(dataRoot);
        Settings = new StaticGatewaySettingsSource(settings ?? new PillionSettings());
        Embedder = embedder ?? new FakeEmbedder();
        Notifier = new IndexStatusNotifier(Sender, TimeProvider.System, NullLogger<IndexStatusNotifier>.Instance);
        var factory = new IndexStoreFactory(Paths, Flags, probe: probe, options: new IndexStoreOptions { ReaderOpenAttempts = 40, ReaderOpenDelay = TimeSpan.FromMilliseconds(100) });
        var retention = new IndexRetention(Paths, Flags, probe: probe, time: time);
        Service = new IndexingService(
            factory,
            retention,
            Embedder,
            Settings,
            Flags,
            Notifier,
            Activity,
            Power,
            memory ?? Memory,
            options ?? new IndexingOptions { MinimumRefreshInterval = TimeSpan.FromMilliseconds(200), PowerPollInterval = TimeSpan.FromMilliseconds(100), MemoryPollInterval = TimeSpan.FromMilliseconds(50), ReaderPollInterval = TimeSpan.FromMilliseconds(200), EmbeddingRetryDelay = TimeSpan.FromMilliseconds(10) },
            pipeName,
            time,
            loggerFactory);
    }

    public PillionPaths Paths { get; }

    public StaticGatewaySettingsSource Settings { get; }

    public IIndexEmbedder Embedder { get; }

    public AllFlags Flags { get; } = new();

    public RecordingStatusSender Sender { get; } = new();

    public IndexStatusNotifier Notifier { get; }

    public IdeActivityMonitor Activity { get; } = new();

    public FakePower Power { get; } = new();

    public FakeMemory Memory { get; } = new();

    public IndexingService Service { get; }

    public async Task StartAsync(string? folder = null)
    {
        await Service.StartAsync(CancellationToken.None);
        if (folder is not null)
        {
            Service.SetWorkspace(new WorkspaceDescription { FolderPath = folder });
        }
    }

    /// <summary>Waits until the status satisfies a condition.</summary>
    public async Task<IndexStatus> WaitForAsync(Func<IndexStatus, bool> condition, TimeSpan? timeout = null, string? what = null)
    {
        var deadline = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(60);
        while (true)
        {
            var status = Service.GetStatus();
            if (condition(status))
            {
                return status;
            }

            if (deadline.Elapsed > limit)
            {
                throw new TimeoutException($"Timed out waiting for {what ?? "the index status"}: phase {status.Phase}, reason {status.PauseReason}, error {status.LastError}, roots {status.Roots.Length}, files {status.IndexedFileCount}/{status.TotalFileCount}.");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>Waits until every root is open as the writer, swept and idle.</summary>
    public Task<IndexStatus> WaitForIdleAsync(TimeSpan? timeout = null) =>
        WaitForAsync(
            s => s.Phase == IndexPhase.Idle && s.Roots.Length > 0 && s.Roots.All(r => r.Role != Pillion.Contracts.IndexRole.Writer || r.LastFullSweepUtc is not null) && Service.OpenRoots.Count == s.Roots.Length,
            timeout,
            "an idle, fully swept index");

    public RootIndex Root => Assert.Single(Service.OpenRoots);

    public async ValueTask DisposeAsync()
    {
        await Service.DisposeAsync();
        Notifier.Dispose();
    }
}

/// <summary>Copies fixture trees into a temporary folder.</summary>
internal static class Fixtures
{
    public static string Directory(string name) => Path.Combine(AppContext.BaseDirectory, "Indexing", "Fixtures", name);

    public static void Copy(string name, string destination)
    {
        var source = Directory(name);
        foreach (var file in System.IO.Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>Gives a file a modified time well in the past, so a later edit is never inside the sweep's racy window.</summary>
    public static void Age(string root)
    {
        var past = DateTime.UtcNow.AddMinutes(-10);
        foreach (var file in System.IO.Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, past);
        }
    }
}

/// <summary>A temporary repository root and data folder for one test.</summary>
internal sealed class IndexingWorkspace : IDisposable
{
    private readonly TempDirectory _temp = new();

    public IndexingWorkspace()
    {
        Repository = Path.Combine(_temp.Path, "repo");
        Data = Path.Combine(_temp.Path, "data");
        System.IO.Directory.CreateDirectory(Repository);
        System.IO.Directory.CreateDirectory(Data);
    }

    public string Repository { get; }

    public string Data { get; }

    public string File(string relative) => Path.Combine(Repository, relative.Replace('/', Path.DirectorySeparatorChar));

    public void Dispose() => _temp.Dispose();
}

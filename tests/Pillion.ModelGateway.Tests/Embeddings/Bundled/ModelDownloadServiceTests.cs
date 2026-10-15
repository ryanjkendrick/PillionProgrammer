using System.Net;
using System.Security.Cryptography;
using System.Text;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Bundled;

namespace Pillion.ModelGateway.Tests.Embeddings.Bundled;

/// <summary>
/// <see cref="ModelDownloadService"/> against a fake server: manifest URLs only, progress, hash and size verification, atomic
/// rename, https-only redirects, cancellation and the Local-only and feature-flag gates.
/// </summary>
public sealed class ModelDownloadServiceTests : IDisposable
{
    private static readonly byte[] Graph = Encoding.UTF8.GetBytes(new string('g', 3_000_000));
    private static readonly byte[] Vocab = Encoding.UTF8.GetBytes("[PAD]\n[UNK]\n[CLS]\n[SEP]\nhello\n");
    private static readonly byte[] Fp16 = Encoding.UTF8.GetBytes("half precision");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pillion-download-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServer _server = new();
    private readonly ModelManifest _manifest = ModelManifest.Parse($$"""
        {
          "version": 1,
          "models": [
            {
              "id": "m", "dimensions": 4, "maxTokens": 16, "pooling": "cls", "tokenizer": "bert-wordpiece",
              "files": [
                { "role": "model", "path": "model.onnx", "url": "https://models.example.test/m/model.onnx", "sha256": "{{Sha(Graph)}}", "size": {{Graph.Length}} },
                { "role": "model-fp16", "path": "model_fp16.onnx", "url": "https://models.example.test/m/model_fp16.onnx", "sha256": "{{Sha(Fp16)}}", "size": {{Fp16.Length}}, "optional": true },
                { "role": "vocab", "path": "vocab.txt", "url": "https://models.example.test/m/vocab.txt", "sha256": "{{Sha(Vocab)}}", "size": {{Vocab.Length}} }
              ]
            },
            {
              "id": "retired", "available": false, "dimensions": 4, "maxTokens": 16, "pooling": "cls", "tokenizer": "bert-wordpiece",
              "files": [
                { "role": "model", "path": "model.onnx", "url": "https://models.example.test/r/model.onnx", "sha256": "{{Sha(Graph)}}", "size": {{Graph.Length}} },
                { "role": "vocab", "path": "vocab.txt", "url": "https://models.example.test/r/vocab.txt", "sha256": "{{Sha(Vocab)}}", "size": {{Vocab.Length}} }
              ]
            }
          ]
        }
        """);

    public ModelDownloadServiceTests()
    {
        _server.Files["https://models.example.test/m/model.onnx"] = Graph;
        _server.Files["https://models.example.test/m/vocab.txt"] = Vocab;
        _server.Files["https://models.example.test/m/model_fp16.onnx"] = Fp16;
    }

    private string ModelFolder => Path.Combine(_root, "m");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Download_FetchesTheRequiredFiles_VerifiesThem_AndReportsProgress()
    {
        var progress = new List<ModelDownloadProgress>();

        var result = await Service().DownloadAsync("m", new SynchronousProgress(progress.Add));

        Assert.Equal(["model.onnx", "vocab.txt"], result.Downloaded);
        Assert.Empty(result.AlreadyPresent);
        Assert.Equal(ModelFolder, result.Directory);
        Assert.Equal(Graph, File.ReadAllBytes(Path.Combine(ModelFolder, "model.onnx")));
        Assert.Equal(Vocab, File.ReadAllBytes(Path.Combine(ModelFolder, "vocab.txt")));
        Assert.False(File.Exists(Path.Combine(ModelFolder, "model_fp16.onnx")));
        Assert.Empty(Directory.GetFiles(ModelFolder, "*.partial"));

        Assert.Contains(progress, p => p.File == "model.onnx" && p.BytesReceived == 0 && p.FileIndex == 1 && p.FileCount == 2);
        Assert.Contains(progress, p => p.File == "model.onnx" && p.BytesReceived == Graph.Length && p.TotalBytes == Graph.Length);
        Assert.Contains(progress, p => p.File == "vocab.txt" && p.BytesReceived == Vocab.Length && p.FileIndex == 2);
    }

    [Fact]
    public async Task ASecondDownload_FindsTheVerifiedFiles_AndRequestsNothing()
    {
        await Service().DownloadAsync("m");
        _server.Requests.Clear();

        var result = await Service().DownloadAsync("m");

        Assert.Empty(result.Downloaded);
        Assert.Equal(["model.onnx", "vocab.txt"], result.AlreadyPresent);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task OptionalFiles_AreFetchedOnlyWhenAskedFor()
    {
        var result = await Service().DownloadAsync("m", includeOptional: true);

        Assert.Equal(["model.onnx", "model_fp16.onnx", "vocab.txt"], result.Downloaded);
        Assert.Equal(Fp16, File.ReadAllBytes(Path.Combine(ModelFolder, "model_fp16.onnx")));
    }

    [Fact]
    public async Task ATamperedDownload_IsRefusedAndDeleted_AndNothingTakesItsName()
    {
        var tampered = (byte[])Graph.Clone();
        tampered[123] = (byte)'x';
        _server.Files["https://models.example.test/m/model.onnx"] = tampered;

        var ex = await Assert.ThrowsAsync<ModelVerificationException>(() => Service().DownloadAsync("m"));

        Assert.Equal(Sha(tampered), ex.ActualSha256);
        Assert.False(File.Exists(Path.Combine(ModelFolder, "model.onnx")));
        Assert.Empty(Directory.GetFiles(ModelFolder, "*.partial"));
    }

    [Fact]
    public async Task ADownloadLongerThanTheManifestSays_IsCutOffAndRefused()
    {
        _server.Files["https://models.example.test/m/vocab.txt"] = [.. Vocab, .. Vocab];

        var ex = await Assert.ThrowsAsync<ModelVerificationException>(() => Service().DownloadAsync("m"));

        Assert.Null(ex.ActualSha256);
        Assert.False(File.Exists(Path.Combine(ModelFolder, "vocab.txt")));
        Assert.Empty(Directory.GetFiles(ModelFolder, "*.partial"));
    }

    [Fact]
    public async Task AFileReplacedOnDisk_IsDownloadedAgain()
    {
        await Service().DownloadAsync("m");
        File.WriteAllText(Path.Combine(ModelFolder, "vocab.txt"), "tampered");

        var result = await Service().DownloadAsync("m");

        Assert.Equal(["vocab.txt"], result.Downloaded);
        Assert.Equal(Vocab, File.ReadAllBytes(Path.Combine(ModelFolder, "vocab.txt")));
    }

    [Fact]
    public async Task HttpsRedirects_AreFollowed_AndOnlyManifestUrlsAreRequestedFirstHand()
    {
        _server.Redirects["https://models.example.test/m/model.onnx"] = "https://cdn.example.test/blobs/abc";
        _server.Files["https://cdn.example.test/blobs/abc"] = Graph;

        await Service().DownloadAsync("m");

        var manifestUrls = _manifest.Models.SelectMany(m => m.Files).Select(f => f.Url.ToString()).ToHashSet();
        Assert.Equal(
            ["https://models.example.test/m/model.onnx", "https://cdn.example.test/blobs/abc", "https://models.example.test/m/vocab.txt"],
            _server.Requests);
        Assert.All(_server.Requests.Where(r => !r.StartsWith("https://cdn.", StringComparison.Ordinal)), r => Assert.Contains(r, manifestUrls));
        Assert.Equal(Graph, File.ReadAllBytes(Path.Combine(ModelFolder, "model.onnx")));
    }

    [Fact]
    public async Task ARedirectToPlainHttp_IsRefused()
    {
        _server.Redirects["https://models.example.test/m/model.onnx"] = "http://cdn.example.test/blobs/abc";
        _server.Files["http://cdn.example.test/blobs/abc"] = Graph;

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Service().DownloadAsync("m"));

        Assert.Contains("only downloaded over https", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("http://cdn.example.test/blobs/abc", _server.Requests);
        Assert.False(File.Exists(Path.Combine(ModelFolder, "model.onnx")));
    }

    [Fact]
    public async Task AnHttpError_FailsTheDownload_WithTheStatus()
    {
        _server.Files.Remove("https://models.example.test/m/vocab.txt");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Service().DownloadAsync("m"));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Empty(Directory.GetFiles(ModelFolder, "*.partial"));
    }

    [Fact]
    public async Task Cancelling_StopsTheDownload_AndDeletesThePartialFile()
    {
        using var cts = new CancellationTokenSource();
        _server.StallAfterFirstChunk = true;
        _server.FirstChunkSent = () => cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().DownloadAsync("m", cancellationToken: cts.Token));

        Assert.False(File.Exists(Path.Combine(ModelFolder, "model.onnx")));
        Assert.Empty(Directory.GetFiles(ModelFolder, "*.partial"));
    }

    [Fact]
    public async Task LocalOnly_DownloadsNothing()
    {
        var settings = new PillionSettings();
        settings.Privacy.LocalOnly = true;

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => Service(settings).DownloadAsync("m"));

        Assert.Equal(PillionErrorCode.PolicyDenied, ex.Error.Code);
        Assert.Contains("Local-only", ex.Error.Message, StringComparison.Ordinal);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task TheFeatureFlagOff_DownloadsNothing()
    {
        var settings = new PillionSettings();
        settings.Features.BundledEmbeddings = false;

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => Service(settings).DownloadAsync("m"));

        Assert.Equal(PillionErrorCode.PolicyDenied, ex.Error.Code);
        Assert.Contains("bundledEmbeddings", ex.Error.Message, StringComparison.Ordinal);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task OnlyModelsOfTheManifest_CanBeDownloaded()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service().DownloadAsync("https://evil.example.test/model.onnx"));
        var retired = await Assert.ThrowsAsync<PillionRpcException>(() => Service().DownloadAsync("retired"));

        Assert.Equal(PillionErrorCode.ModelNotInstalled, retired.Error.Code);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task AFinishedDownload_IsVisibleToTheRuntime()
    {
        using var runtime = new BundledModelRuntime(_manifest, new BundledModelOptions { InstallModelsDirectory = Path.Combine(_root, "none"), UserModelsDirectory = _root, IdleUnloadAfter = TimeSpan.Zero });
        Assert.Null(runtime.Locate("m"));

        await Service(runtime: runtime).DownloadAsync("m");

        Assert.Equal(ModelFolder, runtime.Locate("m")?.Directory);
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private ModelDownloadService Service(PillionSettings? settings = null, BundledModelRuntime? runtime = null) => new(
        _manifest,
        _root,
        runtime,
        settings is null ? null : new StaticGatewaySettingsSource(settings),
        () => _server);

    /// <summary>Reports progress on the caller's thread, so the test sees every report before the call returns.</summary>
    private sealed class SynchronousProgress(Action<ModelDownloadProgress> report) : IProgress<ModelDownloadProgress>
    {
        public void Report(ModelDownloadProgress value) => report(value);
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Redirects { get; } = new(StringComparer.Ordinal);

        public List<string> Requests { get; } = [];

        public bool StallAfterFirstChunk { get; set; }

        public Action? FirstChunkSent { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Requests)
            {
                Requests.Add(url);
            }

            if (request.Method != HttpMethod.Get || request.Headers.Authorization is not null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            }

            if (Redirects.TryGetValue(url, out var target))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(target);
                return Task.FromResult(redirect);
            }

            if (!Files.TryGetValue(url, out var data))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            HttpContent content = StallAfterFirstChunk ? new StreamContent(new StallingStream(data, FirstChunkSent)) : new ByteArrayContent(data);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>Hands out one chunk, then waits until the read is cancelled.</summary>
    private sealed class StallingStream(byte[] data, Action? firstChunkSent) : Stream
    {
        private bool _sent;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                var n = Math.Min(buffer.Length, 4096);
                data.AsMemory(0, n).CopyTo(buffer);
                firstChunkSent?.Invoke();
                return n;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

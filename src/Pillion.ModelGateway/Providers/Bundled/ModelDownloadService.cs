using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Cloud;

namespace Pillion.ModelGateway.Providers.Bundled;

/// <summary>Progress of a model download.</summary>
/// <param name="ModelId">The model.</param>
/// <param name="File">The file being downloaded (its path in the model's folder).</param>
/// <param name="FileIndex">The file's position in the download, from 1.</param>
/// <param name="FileCount">The number of files to download.</param>
/// <param name="BytesReceived">The bytes of this file received so far.</param>
/// <param name="TotalBytes">The size of this file.</param>
public sealed record ModelDownloadProgress(string ModelId, string File, int FileIndex, int FileCount, long BytesReceived, long TotalBytes);

/// <summary>The outcome of a model download.</summary>
/// <param name="ModelId">The model.</param>
/// <param name="Directory">The folder the model's files are in.</param>
/// <param name="Downloaded">The files fetched by this call.</param>
/// <param name="AlreadyPresent">The files that were already there and matched the manifest.</param>
public sealed record ModelDownloadResult(string ModelId, string Directory, IReadOnlyList<string> Downloaded, IReadOnlyList<string> AlreadyPresent);

/// <summary>
/// Downloads the on-request models of the manifest (multilingual E5, Jina code) into the user's models folder
/// (<c>%LOCALAPPDATA%\PillionProgrammer\models\&lt;id&gt;\</c>; work item 2.7). Only URLs from the manifest compiled into this
/// assembly are fetched: callers name a model id, never a URL. Each file is streamed to a temporary file next to its final
/// name while it is hashed, checked against the manifest's size and SHA-256, and renamed into place only when it matches, so
/// a cancelled, interrupted or tampered download never leaves a file the host would load. Requests use the outbound HTTP
/// conventions of the cloud adapters: the system proxy (or the <c>proxy</c> and <c>caBundle</c> of a <c>bundled</c> entry in
/// <c>providers</c>), certificate validation always on, and redirects followed only to https URLs (Hugging Face answers with a
/// redirect to its content store; no credential is ever sent). Nothing is downloaded while Local-only mode is on.
/// </summary>
public sealed partial class ModelDownloadService
{
    private const int MaxRedirects = 5;
    private static readonly TimeSpan HeadersTimeout = TimeSpan.FromSeconds(60);

    private readonly ModelManifest _manifest;
    private readonly BundledModelRuntime? _runtime;
    private readonly IGatewaySettingsSource? _settings;
    private readonly Func<HttpMessageHandler>? _handlerFactory;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="ModelDownloadService"/> class.</summary>
    /// <param name="manifest">The model manifest; the only source of URLs.</param>
    /// <param name="targetRoot">The folder that receives one folder per model (normally <see cref="BundledModelOptions.UserModelsDirectory"/>).</param>
    /// <param name="runtime">The runtime to tell about new files, or <see langword="null"/>.</param>
    /// <param name="settings">The settings (Local-only, the feature flag, and the proxy of a <c>bundled</c> provider entry), or <see langword="null"/> for none.</param>
    /// <param name="handlerFactory">Creates the HTTP handler (tests pass a fake), or <see langword="null"/> for the real one.</param>
    /// <param name="logger">The logger.</param>
    public ModelDownloadService(
        ModelManifest manifest,
        string targetRoot,
        BundledModelRuntime? runtime = null,
        IGatewaySettingsSource? settings = null,
        Func<HttpMessageHandler>? handlerFactory = null,
        ILogger<ModelDownloadService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);

        _manifest = manifest;
        TargetRoot = Path.GetFullPath(targetRoot);
        _runtime = runtime;
        _settings = settings;
        _handlerFactory = handlerFactory;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Gets the folder that receives one folder per model.</summary>
    public string TargetRoot { get; }

    /// <summary>Downloads a model's files that are missing or do not match the manifest.</summary>
    /// <param name="modelId">The model id from the manifest.</param>
    /// <param name="progress">Receives progress, or <see langword="null"/>.</param>
    /// <param name="includeOptional">Whether to fetch the optional files too (the FP16 graph for DirectML).</param>
    /// <param name="cancellationToken">A token that cancels the download; the partial file is deleted.</param>
    /// <returns>What was downloaded.</returns>
    /// <exception cref="ArgumentException">The manifest has no such model.</exception>
    /// <exception cref="PillionRpcException">Local-only mode is on, or the bundled embeddings feature is off (<see cref="PillionErrorCode.PolicyDenied"/>), or the model is not offered.</exception>
    /// <exception cref="ModelVerificationException">A downloaded file does not match the manifest; it has been deleted.</exception>
    /// <exception cref="HttpRequestException">The server could not be reached or answered with an error.</exception>
    public async Task<ModelDownloadResult> DownloadAsync(string modelId, IProgress<ModelDownloadProgress>? progress = null, bool includeOptional = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var model = _manifest.Find(modelId) ?? throw new ArgumentException($"The model manifest has no model '{modelId}'.", nameof(modelId));
        if (!model.Available)
        {
            throw new PillionRpcException(new PillionError { Code = PillionErrorCode.ModelNotInstalled, Message = $"{model.Id} is listed in the model manifest but not offered: {model.Source}" });
        }

        var effective = _settings?.GetCurrent();
        if (effective is not null && effective.LocalOnly)
        {
            throw new PillionRpcException(new PillionError
            {
                Code = PillionErrorCode.PolicyDenied,
                Message = $"Local-only mode is on, so {model.Id} is not downloaded. Copy its files into {Path.Combine(TargetRoot, model.Id)} instead (the offline bundle carries them).",
            });
        }

        if (effective is not null && !effective.IsFeatureEnabled(FeatureNames.BundledEmbeddings))
        {
            throw new PillionRpcException(new PillionError
            {
                Code = PillionErrorCode.PolicyDenied,
                Message = "The bundled embedding models are turned off by settings or policy (feature bundledEmbeddings).",
            });
        }

        var directory = Path.Combine(TargetRoot, model.Id);
        Directory.CreateDirectory(directory);
        var files = model.Files.Where(f => includeOptional || !f.Optional).ToList();
        var downloaded = new List<string>();
        var present = new List<string>();

        var (client, owned) = CreateClient(effective);
        using var disposeClient = client;
        using var disposeOwned = owned;
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var target = Path.Combine(directory, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (IsVerified(target, file, cancellationToken))
            {
                present.Add(file.Path);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var started = Stopwatch.GetTimestamp();
            await DownloadFileAsync(client, model, file, target, i + 1, files.Count, progress, cancellationToken).ConfigureAwait(false);
            var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            LogDownloaded(_logger, model.Id, file.Path, file.Size, seconds);
            downloaded.Add(file.Path);
        }

        _runtime?.Invalidate();
        return new ModelDownloadResult(model.Id, directory, downloaded, present);
    }

    private static bool IsVerified(string path, ModelFile file, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            ModelFiles.Verify(path, file, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is ModelVerificationException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<HttpResponseMessage> GetFollowingHttpsRedirectsAsync(HttpClient client, Uri url, CancellationToken cancellationToken)
    {
        var current = url;
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PillionProgrammer", typeof(ModelDownloadService).Assembly.GetName().Version?.ToString(3) ?? "0"));
            using var headers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headers.CancelAfter(HeadersTimeout);
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new HttpRequestException($"{current.Host} did not answer within {HeadersTimeout.TotalSeconds:F0} s.", ex);
            }

            var status = (int)response.StatusCode;
            if (status is < 300 or >= 400)
            {
                return response;
            }

            using (response)
            {
                var location = response.Headers.Location;
                if (location is null || hop >= MaxRedirects)
                {
                    throw new HttpRequestException(string.Create(CultureInfo.InvariantCulture, $"{current.Host} answered HTTP {status} without a usable redirect."), null, response.StatusCode);
                }

                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (next.Scheme != Uri.UriSchemeHttps)
                {
                    throw new HttpRequestException($"{current.Host} redirected to {next.Scheme}://{next.Host}; model files are only downloaded over https.");
                }

                current = next;
            }
        }
    }

    private (HttpClient Client, IDisposable? Owned) CreateClient(EffectiveSettings? effective)
    {
        var entry = effective?.Settings.Providers.FirstOrDefault(p => p.Kind == ProviderKind.Bundled)
            ?? new ProviderSettings { Id = BundledEmbeddingProvider.DefaultId, Kind = ProviderKind.Bundled };
        var client = CloudTransport.CreateClient(entry, _handlerFactory, out var owned);
        return (client, owned);
    }

    private async Task DownloadFileAsync(HttpClient client, ManifestModel model, ModelFile file, string target, int index, int count, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var partial = target + "." + Guid.NewGuid().ToString("N")[..8] + ".partial";
        var moved = false;
        try
        {
            using var response = await GetFollowingHttpsRedirectsAsync(client, file.Url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new HttpRequestException(string.Create(CultureInfo.InvariantCulture, $"Downloading {file.Path} of {model.Id} from {file.Url.Host} failed with HTTP {(int)response.StatusCode}."), null, response.StatusCode);
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                var buffer = new byte[1 << 16];
                int read;
                var lastReport = 0L;
                progress?.Report(new ModelDownloadProgress(model.Id, file.Path, index, count, 0, file.Size));
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > file.Size)
                    {
                        // Never write more than the manifest promises.
                        throw new ModelVerificationException(target, file.Sha256, null, file.Size, received);
                    }

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    if (received - lastReport >= 1 << 20 || received == file.Size)
                    {
                        lastReport = received;
                        progress?.Report(new ModelDownloadProgress(model.Id, file.Path, index, count, received, file.Size));
                    }
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (received != file.Size)
            {
                throw new ModelVerificationException(target, file.Sha256, null, file.Size, received);
            }

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, file.Sha256, StringComparison.Ordinal))
            {
                throw new ModelVerificationException(target, file.Sha256, actual, file.Size, received);
            }

            // Same folder, so the rename is atomic: the final name only ever holds a verified file.
            File.Move(partial, target, overwrite: true);
            moved = true;
        }
        catch (ModelVerificationException ex)
        {
            LogRefused(_logger, model.Id, file.Path, file.Url.ToString(), ex.Message);
            throw;
        }
        finally
        {
            if (!moved)
            {
                try
                {
                    File.Delete(partial);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogCleanupFailed(_logger, partial, ex.Message);
                }
            }
        }
    }

    [LoggerMessage(EventId = 476, Level = LogLevel.Information, Message = "Downloaded {File} of model {Model} ({Bytes} bytes) in {Seconds:F1} s and verified it against the model manifest.")]
    private static partial void LogDownloaded(ILogger logger, string model, string file, long bytes, double seconds);

    [LoggerMessage(EventId = 477, Level = LogLevel.Warning, Message = "The download of {File} of model {Model} from {Url} was refused and deleted: {Reason}")]
    private static partial void LogRefused(ILogger logger, string model, string file, string url, string reason);

    [LoggerMessage(EventId = 478, Level = LogLevel.Warning, Message = "The partial download {Path} could not be deleted: {Reason}")]
    private static partial void LogCleanupFailed(ILogger logger, string path, string reason);
}

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Pillion.ModelGateway.Providers.Bundled;

/// <summary>Where a model's verified files are.</summary>
/// <param name="Model">The manifest entry.</param>
/// <param name="Directory">The folder holding the files (<c>&lt;root&gt;\&lt;model id&gt;</c>).</param>
/// <param name="HasFp16">Whether the optional FP16 graph is there and verified too.</param>
public sealed record LocatedModel(ManifestModel Model, string Directory, bool HasFp16)
{
    /// <summary>Gets the path of a file of the model.</summary>
    /// <param name="file">The file.</param>
    /// <returns>The path.</returns>
    public string PathOf(ModelFile file) => System.IO.Path.Combine(Directory, file.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
}

/// <summary>
/// Finds a model's files: in the install folder first (the models shipped in the VSIX), then in the user's models folder (the
/// models downloaded on request). Every required file must match the manifest's size and SHA-256; a folder with a file that
/// does not is skipped with a warning and the next one is tried. Verdicts are kept for the life of the process, keyed by the
/// file's path, size and time stamp, so each file is hashed once per host start (and again only if it changes).
/// </summary>
public sealed partial class BundledModelLocator
{
    private readonly ConcurrentDictionary<(string Path, long Length, DateTime Modified, string Sha256), string?> _verdicts = new();
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="BundledModelLocator"/> class.</summary>
    /// <param name="roots">The folders to search, in order; each holds one folder per model id.</param>
    /// <param name="logger">Receives refusals.</param>
    public BundledModelLocator(IEnumerable<string> roots, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        Roots = roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Gets the folders searched, in order.</summary>
    public IReadOnlyList<string> Roots { get; }

    /// <summary>Finds a model whose required files are all present and verified. The first call for a file hashes it (about 0.1 s for the bundled model).</summary>
    /// <param name="model">The model.</param>
    /// <param name="cancellationToken">A token that cancels the hashing.</param>
    /// <returns>Where the model is, or <see langword="null"/> when no folder has a complete, verified copy.</returns>
    public LocatedModel? Locate(ManifestModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        foreach (var root in Roots)
        {
            var directory = Path.Combine(root, model.Id);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var complete = true;
            foreach (var file in model.RequiredFiles)
            {
                if (Verify(Path.Combine(directory, file.Path), file, cancellationToken) is not null)
                {
                    complete = false;
                    break;
                }
            }

            if (!complete)
            {
                continue;
            }

            var fp16 = model.Find(ModelFileRole.ModelFp16);
            var hasFp16 = fp16 is not null &&
                File.Exists(Path.Combine(directory, fp16.Path)) &&
                Verify(Path.Combine(directory, fp16.Path), fp16, cancellationToken) is null;
            return new LocatedModel(model, directory, hasFp16);
        }

        return null;
    }

    /// <summary>Forgets every verdict, so that the next lookup hashes again (after a download).</summary>
    public void Invalidate() => _verdicts.Clear();

    /// <summary>Checks one file, using the verdict of an earlier check when the file has not changed since.</summary>
    /// <returns><see langword="null"/> when the file matches; otherwise why not.</returns>
    private string? Verify(string path, ModelFile file, CancellationToken cancellationToken)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                return "missing";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }

        var key = (info.FullName, info.Length, info.LastWriteTimeUtc, file.Sha256);
        if (_verdicts.TryGetValue(key, out var verdict))
        {
            return verdict;
        }

        try
        {
            ModelFiles.Verify(info.FullName, file, cancellationToken);
            verdict = null;
            LogVerified(_logger, info.FullName);
        }
        catch (ModelVerificationException ex)
        {
            verdict = ex.Message;
            LogRefused(_logger, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable now is not a verdict on the content: try again next time.
            LogUnreadable(_logger, info.FullName, ex.Message);
            return ex.Message;
        }

        _verdicts[key] = verdict;
        return verdict;
    }

    [LoggerMessage(EventId = 470, Level = LogLevel.Debug, Message = "Model file {Path} matches the model manifest.")]
    private static partial void LogVerified(ILogger logger, string path);

    [LoggerMessage(EventId = 471, Level = LogLevel.Warning, Message = "{Reason}")]
    private static partial void LogRefused(ILogger logger, string reason);

    [LoggerMessage(EventId = 472, Level = LogLevel.Warning, Message = "Model file {Path} could not be read: {Reason}")]
    private static partial void LogUnreadable(ILogger logger, string path, string reason);
}

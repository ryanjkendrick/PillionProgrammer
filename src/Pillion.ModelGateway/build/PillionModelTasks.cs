// MSBuild inline tasks for BundledModels.targets (compiled by RoslynCodeTaskFactory, not part of Pillion.ModelGateway).
// Written for netstandard2.0 and C# 7.3 so that they run under both Visual Studio's MSBuild and `dotnet build`.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

/// <summary>Lists the files of the bundled models in the manifest, with their cache paths and whether each must be downloaded.</summary>
public sealed class PillionResolveModelFiles : Task
{
    [Required]
    public string ManifestPath { get; set; }

    [Required]
    public string CacheRoot { get; set; }

    [Output]
    public ITaskItem[] Files { get; set; }

    public override bool Execute()
    {
        XElement root;
        try
        {
            var bytes = File.ReadAllBytes(ManifestPath);
            using (var reader = JsonReaderWriterFactory.CreateJsonReader(bytes, XmlDictionaryReaderQuotas.Max))
            {
                root = XElement.Load(reader);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is XmlException)
        {
            Log.LogError("The model manifest {0} could not be read: {1}", ManifestPath, ex.Message);
            return false;
        }

        var files = new List<ITaskItem>();
        var models = root.Element("models");
        foreach (var model in models == null ? Enumerable.Empty<XElement>() : models.Elements("item"))
        {
            if (!string.Equals((string)model.Element("bundled"), "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals((string)model.Element("available"), "false", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = (string)model.Element("id");
            var fileList = model.Element("files");
            foreach (var file in fileList == null ? Enumerable.Empty<XElement>() : fileList.Elements("item"))
            {
                if (string.Equals((string)file.Element("optional"), "true", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var path = (string)file.Element("path");
                var url = (string)file.Element("url");
                var sha = ((string)file.Element("sha256") ?? string.Empty).ToLowerInvariant();
                var size = (string)file.Element("size");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(path) || sha.Length != 64 || string.IsNullOrEmpty(url) ||
                    !url.StartsWith("https://", StringComparison.Ordinal) || path.Contains("..") || Path.IsPathRooted(path))
                {
                    Log.LogError("The model manifest {0} has an invalid file entry in model '{1}' ('{2}'): every file needs a relative path, an https URL and a 64-digit SHA-256.", ManifestPath, id, path);
                    return false;
                }

                var directory = Path.Combine(CacheRoot, id, sha.Substring(0, 16));
                var fullPath = Path.Combine(directory, Path.GetFileName(path));
                var item = new TaskItem(fullPath);
                item.SetMetadata("ModelId", id);
                item.SetMetadata("Url", url);
                item.SetMetadata("Sha256", sha);
                item.SetMetadata("Size", size ?? string.Empty);
                item.SetMetadata("TargetPath", Path.Combine("models", id, path.Replace('/', Path.DirectorySeparatorChar)));
                item.SetMetadata("CacheDirectory", directory + Path.DirectorySeparatorChar);
                // A name of its own per build, so that two builds downloading at once (the solution build and the VSIX's host publish) never write the same file.
                item.SetMetadata("PartialName", Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".partial");
                item.SetMetadata("NeedsDownload", File.Exists(fullPath) ? "false" : "true");
                files.Add(item);
            }
        }

        if (files.Count == 0)
        {
            Log.LogWarning("The model manifest {0} lists no bundled model; no model is copied to the output.", ManifestPath);
        }

        Files = files.ToArray();
        return true;
    }
}

/// <summary>Verifies cached and freshly downloaded model files against the manifest; moves verified downloads into place.</summary>
public sealed class PillionVerifyModelFiles : Task
{
    [Required]
    public ITaskItem[] Files { get; set; }

    public override bool Execute()
    {
        var ok = true;
        foreach (var file in Files)
        {
            var final = file.ItemSpec;
            var partial = Path.Combine(file.GetMetadata("CacheDirectory"), file.GetMetadata("PartialName"));
            var expected = file.GetMetadata("Sha256");
            long expectedSize;
            long.TryParse(file.GetMetadata("Size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out expectedSize);
            var url = file.GetMetadata("Url");
            var target = file.GetMetadata("TargetPath");

            if (File.Exists(final))
            {
                if (StampMatches(final, expected))
                {
                    continue;
                }

                string actual;
                long size;
                Hash(final, out actual, out size);
                if (actual != expected || (expectedSize > 0 && size != expectedSize))
                {
                    TryDelete(final);
                    TryDelete(final + ".sha256");
                    Log.LogError(
                        "The cached model file {0} for {1} has SHA-256 {2} ({3} bytes), but models.manifest.json expects {4} ({5} bytes). The file has been deleted; build again to download it from {6}.",
                        final, target, actual, size, expected, expectedSize, url);
                    ok = false;
                    continue;
                }

                WriteStamp(final, expected);
                continue;
            }

            if (!File.Exists(partial))
            {
                Log.LogError("The model file {0} was not downloaded from {1}.", target, url);
                ok = false;
                continue;
            }

            string downloaded;
            long downloadedSize;
            Hash(partial, out downloaded, out downloadedSize);
            if (downloaded != expected || (expectedSize > 0 && downloadedSize != expectedSize))
            {
                TryDelete(partial);
                Log.LogError(
                    "The model file {0} downloaded from {1} has SHA-256 {2} ({3} bytes), but models.manifest.json expects {4} ({5} bytes). It has been deleted and the build stops: either the download was corrupted (build again) or the file behind the URL changed, which a pinned Hugging Face commit should never do.",
                    target, url, downloaded, downloadedSize, expected, expectedSize);
                ok = false;
                continue;
            }

            try
            {
                File.Move(partial, final);
            }
            catch (IOException) when (File.Exists(final))
            {
                // Another build verified and moved the same file first; its copy has the same hash.
                TryDelete(partial);
            }

            WriteStamp(final, expected);
            Log.LogMessage(MessageImportance.High, "Verified {0} (SHA-256 {1}).", target, expected);
        }

        return ok && !Log.HasLoggedErrors;
    }

    private static void Hash(string path, out string hash, out long size)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
        using (var sha = SHA256.Create())
        {
            var digest = sha.ComputeHash(stream);
            var text = new StringBuilder(digest.Length * 2);
            foreach (var b in digest)
            {
                text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }

            hash = text.ToString();
            size = stream.Length;
        }
    }

    private static string StampText(string path, string hash)
    {
        var info = new FileInfo(path);
        return hash + " " + info.Length.ToString(CultureInfo.InvariantCulture) + " " + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
    }

    private static bool StampMatches(string path, string hash)
    {
        try
        {
            var stamp = path + ".sha256";
            return File.Exists(stamp) && string.Equals(File.ReadAllText(stamp).Trim(), StampText(path, hash), StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void WriteStamp(string path, string hash)
    {
        try
        {
            File.WriteAllText(path + ".sha256", StampText(path, hash));
        }
        catch (IOException)
        {
            // The stamp only saves time; without it the next build hashes the file again.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

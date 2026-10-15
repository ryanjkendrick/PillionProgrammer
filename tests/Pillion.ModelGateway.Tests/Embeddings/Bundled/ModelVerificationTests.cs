using System.Security.Cryptography;
using System.Text;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Bundled;

namespace Pillion.ModelGateway.Tests.Embeddings.Bundled;

/// <summary>Hash verification: a file whose size or SHA-256 differs from the manifest is refused, wherever it is found and whenever it changes.</summary>
public sealed class ModelVerificationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pillion-verify-" + Guid.NewGuid().ToString("N"));

    public ModelVerificationTests() => Directory.CreateDirectory(_root);

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
    public void ReadVerified_ReturnsTheBytes_OfAMatchingFile()
    {
        var (manifest, files) = SyntheticModel();
        var path = Write("a", "m", "model.onnx", files["model.onnx"]);

        var bytes = ModelFiles.ReadVerified(path, manifest.Models[0].Find(ModelFileRole.Model)!);

        Assert.Equal(files["model.onnx"], bytes);
    }

    [Fact]
    public void ATamperedFile_IsRejected_WithBothHashes()
    {
        var (manifest, files) = SyntheticModel();
        var tampered = (byte[])files["model.onnx"].Clone();
        tampered[3] ^= 0x01;
        var path = Write("a", "m", "model.onnx", tampered);
        var file = manifest.Models[0].Find(ModelFileRole.Model)!;

        var ex = Assert.Throws<ModelVerificationException>(() => ModelFiles.ReadVerified(path, file));
        Assert.Throws<ModelVerificationException>(() => ModelFiles.Verify(path, file, CancellationToken.None));

        Assert.Equal(file.Sha256, ex.ExpectedSha256);
        Assert.Equal(Sha(tampered), ex.ActualSha256);
        Assert.Contains("refused", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileOfTheWrongSize_IsRejected_BeforeHashing()
    {
        var (manifest, files) = SyntheticModel();
        var path = Write("a", "m", "model.onnx", [.. files["model.onnx"], 0]);

        var ex = Assert.Throws<ModelVerificationException>(() => ModelFiles.Verify(path, manifest.Models[0].Find(ModelFileRole.Model)!, CancellationToken.None));

        Assert.Null(ex.ActualSha256);
        Assert.Equal(ex.ExpectedSize + 1, ex.ActualSize);
    }

    [Fact]
    public void TheLocator_SkipsAFolderWithATamperedFile_AndUsesTheNextOne()
    {
        var (manifest, files) = SyntheticModel();
        var tampered = (byte[])files["vocab.txt"].Clone();
        tampered[0] ^= 0x20;
        WriteModel("install", files, ("vocab.txt", tampered));
        WriteModel("user", files);
        var locator = new BundledModelLocator([Path.Combine(_root, "install"), Path.Combine(_root, "user")]);

        var located = locator.Locate(manifest.Models[0], CancellationToken.None);

        Assert.NotNull(located);
        Assert.Equal(Path.Combine(_root, "user", "m"), located.Directory);
        Assert.False(located.HasFp16);
    }

    [Fact]
    public void TheLocator_FindsNothing_WhenEveryCopyIsTamperedOrIncomplete()
    {
        var (manifest, files) = SyntheticModel();
        var tampered = (byte[])files["model.onnx"].Clone();
        tampered[^1] ^= 0xFF;
        WriteModel("install", files, ("model.onnx", tampered));
        Write("user", "m", "model.onnx", files["model.onnx"]); // vocab.txt missing
        var locator = new BundledModelLocator([Path.Combine(_root, "install"), Path.Combine(_root, "user")]);

        Assert.Null(locator.Locate(manifest.Models[0], CancellationToken.None));
    }

    [Fact]
    public void TheLocator_HashesAgain_WhenAFileChanges()
    {
        var (manifest, files) = SyntheticModel();
        WriteModel("install", files);
        var locator = new BundledModelLocator([Path.Combine(_root, "install")]);
        Assert.NotNull(locator.Locate(manifest.Models[0], CancellationToken.None));

        var tampered = (byte[])files["model.onnx"].Clone();
        tampered[0] ^= 0x01;
        var path = Write("install", "m", "model.onnx", tampered);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        Assert.Null(locator.Locate(manifest.Models[0], CancellationToken.None));
    }

    [Fact]
    public void TheRuntime_RefusesAGraphTamperedAfterItWasLocated_BecauseItLoadsTheBytesItVerifies()
    {
        var source = Path.Combine(TestModels.RequireBuildOutput(), TestModels.Bge);
        var copy = Path.Combine(_root, "models", TestModels.Bge);
        Directory.CreateDirectory(copy);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        }

        using var runtime = TestModels.Runtime(Path.Combine(_root, "models"));
        Assert.NotNull(runtime.Locate(TestModels.Bge));

        // Same size and time stamp, different content: the locator's cached verdict still says yes, the load must not.
        var graph = Path.Combine(copy, "model_quantized.onnx");
        var stamp = File.GetLastWriteTimeUtc(graph);
        using (var stream = new FileStream(graph, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = 1000;
            var b = stream.ReadByte();
            stream.Position = 1000;
            stream.WriteByte((byte)(b ^ 0x5A));
        }

        File.SetLastWriteTimeUtc(graph, stamp);

        var ex = Assert.Throws<ProviderException>(() => runtime.Embed(TestModels.Bge, ["hello"], CancellationToken.None));
        Assert.Equal(PillionErrorCode.ModelNotInstalled, ex.Code);
        Assert.IsType<ModelVerificationException>(ex.InnerException);

        // The refusal forgets the verdicts, so the next lookup hashes the file again and refuses it too.
        Assert.Null(runtime.Locate(TestModels.Bge));
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static (ModelManifest Manifest, Dictionary<string, byte[]> Files) SyntheticModel()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["model.onnx"] = Encoding.UTF8.GetBytes("not really a graph, but bytes to hash"),
            ["vocab.txt"] = Encoding.UTF8.GetBytes("[PAD]\n[UNK]\n[CLS]\n[SEP]\nhello\n"),
            ["model_fp16.onnx"] = Encoding.UTF8.GetBytes("fp16"),
        };
        var json = $$"""
            {
              "version": 1,
              "models": [
                {
                  "id": "m", "dimensions": 4, "maxTokens": 16, "pooling": "mean", "tokenizer": "bert-wordpiece",
                  "files": [
                    { "role": "model", "path": "model.onnx", "url": "https://example.test/model.onnx", "sha256": "{{Sha(files["model.onnx"])}}", "size": {{files["model.onnx"].Length}} },
                    { "role": "model-fp16", "path": "model_fp16.onnx", "url": "https://example.test/model_fp16.onnx", "sha256": "{{Sha(files["model_fp16.onnx"])}}", "size": {{files["model_fp16.onnx"].Length}}, "optional": true },
                    { "role": "vocab", "path": "vocab.txt", "url": "https://example.test/vocab.txt", "sha256": "{{Sha(files["vocab.txt"])}}", "size": {{files["vocab.txt"].Length}} }
                  ]
                }
              ]
            }
            """;
        return (ModelManifest.Parse(json), files);
    }

    private void WriteModel(string root, Dictionary<string, byte[]> files, params (string Name, byte[] Data)[] overrides)
    {
        foreach (var (name, data) in files.Where(f => f.Key != "model_fp16.onnx"))
        {
            Write(root, "m", name, overrides.FirstOrDefault(o => o.Name == name).Data ?? data);
        }
    }

    private string Write(string root, string model, string name, byte[] data)
    {
        var directory = Path.Combine(_root, root, model);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, data);
        return path;
    }
}

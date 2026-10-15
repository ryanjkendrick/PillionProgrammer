using System.Diagnostics;
using Pillion.Indexing.Discovery;

namespace Pillion.Indexing.Tests;

/// <summary>Every exclusion rule of the scanner, with the reason it reports.</summary>
public sealed class ScannerTests
{
    private static async Task<Dictionary<string, ScanEntry>> ScanAsync(TempDirectory temp, ScanOptions? options = null)
    {
        var entries = await new RepositoryScanner(options ?? new ScanOptions { MaxDegreeOfParallelism = 2 }).ScanToListAsync(temp.Path);
        return entries.ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);
    }

    private static void AssertIncluded(Dictionary<string, ScanEntry> entries, string path)
    {
        Assert.True(entries.TryGetValue(path, out var entry), $"{path} was not listed");
        Assert.True(entry.Decision.IsIncluded, $"{path}: {entry.Decision.Describe()}");
    }

    private static void AssertExcluded(Dictionary<string, ScanEntry> entries, string path, ExclusionReason reason)
    {
        Assert.True(entries.TryGetValue(path, out var entry), $"{path} was not listed");
        Assert.Equal(reason, entry.Decision.Reason);
    }

    [Fact]
    public async Task OrdinaryFiles_AreIncluded_WithLanguageSizeAndModifiedTime()
    {
        using var temp = new TempDirectory();
        temp.Write("src/Program.cs", "class P {}");
        temp.Write("web/app.ts", "export {}");
        temp.Write("README.md", "# hi");
        temp.Write("Makefile", "all:");
        var written = new DateTimeOffset(File.GetLastWriteTimeUtc(temp.File("src/Program.cs")), TimeSpan.Zero).ToUnixTimeMilliseconds();

        var entries = await ScanAsync(temp);

        Assert.Equal(4, entries.Count);
        Assert.All(entries.Values, e => Assert.True(e.Decision.IsIncluded));
        Assert.Equal(Languages.CSharp, entries["src/Program.cs"].Language);
        Assert.Equal(Languages.TypeScript, entries["web/app.ts"].Language);
        Assert.Equal(Languages.Other, entries["README.md"].Language);
        Assert.Equal(Languages.Other, entries["Makefile"].Language);
        Assert.Equal(10, entries["src/Program.cs"].Size);
        Assert.Equal(written, entries["src/Program.cs"].ModifiedUnixMilliseconds);
        Assert.All(entries.Values, e => Assert.False(e.IsDirectory));
    }

    [Theory]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData("node_modules")]
    [InlineData(".vs")]
    [InlineData("BIN")]
    public async Task TheBuiltInFolders_AreExcludedOnceAndNotEntered_AtAnyDepth(string folder)
    {
        using var temp = new TempDirectory();
        temp.Write("src/keep.cs");
        temp.Write($"{folder}/a.cs");
        temp.Write($"src/lib/{folder}/deep/b.cs");

        var entries = await ScanAsync(temp);

        AssertIncluded(entries, "src/keep.cs");
        AssertExcluded(entries, folder, ExclusionReason.BuiltIn);
        AssertExcluded(entries, $"src/lib/{folder}", ExclusionReason.BuiltIn);
        Assert.True(entries[folder].IsDirectory);
        Assert.DoesNotContain(entries.Keys, k => k.EndsWith("a.cs", StringComparison.Ordinal) || k.EndsWith("b.cs", StringComparison.Ordinal));
        Assert.Equal(folder.ToLowerInvariant(), entries[folder].Decision.Detail);
    }

    [Theory]
    [InlineData("packages/repositories.config")]
    [InlineData("packages/Newtonsoft.Json.13.0.1/Newtonsoft.Json.13.0.1.nupkg")]
    [InlineData("packages/Some.Package/1.0.0/some.package.1.0.0.nupkg")]
    [InlineData("packages/loose.NUPKG")]
    public async Task ARootPackagesFolderThatLooksLikeNuGet_IsExcluded(string marker)
    {
        using var temp = new TempDirectory();
        temp.Write("src/keep.cs");
        temp.Write(marker, "x");
        temp.Write("packages/Newtonsoft.Json.13.0.1/lib/net45/Newtonsoft.Json.dll.txt");

        var entries = await ScanAsync(temp);

        AssertIncluded(entries, "src/keep.cs");
        AssertExcluded(entries, "packages", ExclusionReason.BuiltIn);
        Assert.True(entries["packages"].IsDirectory);
        Assert.Equal("packages", entries["packages"].Decision.Detail);
        Assert.DoesNotContain(entries.Keys, k => k.StartsWith("packages/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APackagesFolderOfSource_IsIndexed_AnywhereItIs()
    {
        using var temp = new TempDirectory();
        temp.Write("packages/app/src/index.ts", "export {}");
        temp.Write("packages/lib/package.json", "{}");
        temp.Write("packages/deep/a/b/c/x.nupkg", "too deep to count");
        temp.Write("services/packages/readme.md", "# docs");

        var entries = await ScanAsync(temp);

        AssertIncluded(entries, "packages/app/src/index.ts");
        AssertIncluded(entries, "packages/lib/package.json");
        AssertIncluded(entries, "packages/deep/a/b/c/x.nupkg");
        AssertIncluded(entries, "services/packages/readme.md");
        Assert.DoesNotContain(entries.Values, e => e.Decision.Reason == ExclusionReason.BuiltIn);
    }

    [Fact]
    public async Task ANuGetLookingPackagesFolderBelowTheRoot_IsSource()
    {
        using var temp = new TempDirectory();
        temp.Write("src/packages/repositories.config", "<repositories/>");
        temp.Write("src/packages/Foo.1.0.0/Foo.1.0.0.nupkg", "x");

        var entries = await ScanAsync(temp);

        AssertIncluded(entries, "src/packages/repositories.config");
        AssertIncluded(entries, "src/packages/Foo.1.0.0/Foo.1.0.0.nupkg");
    }

    [Fact]
    public async Task APackagesFileAtTheRoot_IsNotABuiltIn()
    {
        using var temp = new TempDirectory();
        temp.Write("packages", "a file called packages");

        var entries = await ScanAsync(temp);

        AssertIncluded(entries, "packages");
    }

    [Fact]
    public async Task APillionIgnoreNegation_ReIncludesABuiltInName_SoAMonorepoCanOptBackIn()
    {
        using var temp = new TempDirectory();
        temp.Write(".pillionignore", "!packages/\n!tools/bin/\n!vendor/node_modules/\n");
        temp.Write("packages/repositories.config", "<repositories/>");
        temp.Write("packages/lib/x.ts", "export {}");
        temp.Write("tools/bin/run.cs", "class R {}");
        temp.Write("vendor/node_modules/pkg/index.js", "x");
        temp.Write("other/bin/skipped.cs", "class S {}");
        temp.Write("node_modules/skipped.js", "x");

        var entries = await ScanAsync(temp);

        AssertIncluded(entries, "packages/repositories.config");
        AssertIncluded(entries, "packages/lib/x.ts");
        AssertIncluded(entries, "tools/bin/run.cs");
        AssertIncluded(entries, "vendor/node_modules/pkg/index.js");
        AssertExcluded(entries, "other/bin", ExclusionReason.BuiltIn);
        AssertExcluded(entries, "node_modules", ExclusionReason.BuiltIn);
    }

    [Fact]
    public async Task ANegationOfAFolderName_ReIncludesItAtAnyDepth_ButNotTheGitFolder()
    {
        using var temp = new TempDirectory();
        temp.Write(".pillionignore", "!obj/\n!.git/\n");
        temp.Write("a/obj/x.cs", "class X {}");
        temp.Write(".git/config", "[core]");

        var entries = await ScanAsync(temp);

        AssertIncluded(entries, "a/obj/x.cs");
        AssertExcluded(entries, ".git", ExclusionReason.BuiltIn);
    }

    [Fact]
    public async Task OnlyAPillionIgnoreNegation_ReIncludesBuiltIns_NotTheSettingOrTheSecretList()
    {
        using var temp = new TempDirectory();
        temp.Write(".pillionignore", "!generated/\n!.env\n!secrets.json\n!obj/\n");
        temp.Write("generated/a.cs", "class A {}");
        temp.Write(".env", "KEY=1");
        temp.Write("secrets.json", "{}");
        temp.Write("obj/b.cs", "class B {}");

        // indexing.exclude cannot be undone by a .pillionignore negation, and neither can the secret list.
        var entries = await ScanAsync(temp, new ScanOptions { ExcludePatterns = ["generated/"], MaxDegreeOfParallelism = 2 });

        AssertExcluded(entries, "generated", ExclusionReason.UserExclude);
        AssertExcluded(entries, ".env", ExclusionReason.SecretBearing);
        AssertExcluded(entries, "secrets.json", ExclusionReason.SecretBearing);
        AssertIncluded(entries, "obj/b.cs");

        // And a negation inside indexing.exclude does not re-include a built-in.
        temp.Write("bin/c.cs", "class C {}");
        var viaSetting = await ScanAsync(temp, new ScanOptions { ExcludePatterns = ["!bin/"], MaxDegreeOfParallelism = 2 });
        AssertExcluded(viaSetting, "bin", ExclusionReason.BuiltIn);
    }

    [Fact]
    public void TheNuGetCheck_LooksOneAndTwoLevelsDown()
    {
        using var temp = new TempDirectory();
        temp.Write("a/id.1.0.0/id.1.0.0.nupkg", "x");
        temp.Write("b/id/1.0.0/id.1.0.0.nupkg", "x");
        temp.Write("c/id/1.0.0/lib/x.nupkg", "x");
        temp.Write("d/repositories.config", "x");
        temp.MakeDirectory("e");

        Assert.True(NameRules.LooksLikeNuGetPackagesFolder(temp.File("a")));
        Assert.True(NameRules.LooksLikeNuGetPackagesFolder(temp.File("b")));
        Assert.False(NameRules.LooksLikeNuGetPackagesFolder(temp.File("c")));
        Assert.True(NameRules.LooksLikeNuGetPackagesFolder(temp.File("d")));
        Assert.False(NameRules.LooksLikeNuGetPackagesFolder(temp.File("e")));
        Assert.False(NameRules.LooksLikeNuGetPackagesFolder(temp.File("missing")));
    }

    [Fact]
    public async Task TheGitFolder_IsExcluded_AndAGitFileIsToo()
    {
        using var temp = new TempDirectory();
        temp.Write(".git/config", "[core]");
        temp.Write("wt/.git", "gitdir: ../.git/worktrees/wt");
        temp.Write("keep.cs");

        var entries = await ScanAsync(temp);

        AssertExcluded(entries, ".git", ExclusionReason.BuiltIn);
        AssertIncluded(entries, "keep.cs");
        Assert.DoesNotContain(entries.Keys, k => k.StartsWith(".git/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GitIgnoreRules_ApplyAtEveryLevel_WithTheirReason()
    {
        using var temp = new TempDirectory();
        temp.Write(".gitignore", "*.log\n/out/\ntemp.txt\n");
        temp.Write("a.log");
        temp.Write("out/result.txt");
        temp.Write("src/out/keep.txt");
        temp.Write("src/temp.txt");
        temp.Write("src/.gitignore", "*.cs\n!Keep.cs\n");
        temp.Write("src/x.cs");
        temp.Write("src/Keep.cs");
        temp.Write("src/deep/y.cs");
        temp.Write("other/y.cs");

        var entries = await ScanAsync(temp);

        AssertExcluded(entries, "a.log", ExclusionReason.GitIgnore);
        AssertExcluded(entries, "out", ExclusionReason.GitIgnore);
        AssertIncluded(entries, "src/out/keep.txt");
        AssertExcluded(entries, "src/temp.txt", ExclusionReason.GitIgnore);
        AssertExcluded(entries, "src/x.cs", ExclusionReason.GitIgnore);
        AssertIncluded(entries, "src/Keep.cs");
        AssertExcluded(entries, "src/deep/y.cs", ExclusionReason.GitIgnore);
        AssertIncluded(entries, "other/y.cs");
        Assert.Equal(".gitignore:1: *.log", entries["a.log"].Decision.Detail);
        Assert.Equal("src/.gitignore:1: *.cs", entries["src/x.cs"].Decision.Detail);
        Assert.Contains("src/.gitignore", entries["src/x.cs"].Decision.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APillionIgnoreAtTheRoot_ExcludesWithItsOwnReason_AndANegationReIncludesGitIgnoredFiles()
    {
        using var temp = new TempDirectory();
        temp.Write(".gitignore", "*.gen.json\ngenerated/\n");
        temp.Write(".pillionignore", "docs/\n!generated/\n!keep.gen.json\n*.snap\n");
        temp.Write("docs/guide.md");
        temp.Write("a.snap");
        temp.Write("generated/schema.json");
        temp.Write("keep.gen.json");
        temp.Write("drop.gen.json");
        temp.Write("sub/.pillionignore", "everything.txt\n");
        temp.Write("sub/everything.txt");

        var entries = await ScanAsync(temp);

        AssertExcluded(entries, "docs", ExclusionReason.PillionIgnore);
        AssertExcluded(entries, "a.snap", ExclusionReason.PillionIgnore);
        AssertIncluded(entries, "generated/schema.json");
        AssertIncluded(entries, "keep.gen.json");
        AssertExcluded(entries, "drop.gen.json", ExclusionReason.GitIgnore);

        // Only the root's .pillionignore is read.
        AssertIncluded(entries, "sub/everything.txt");
    }

    [Fact]
    public async Task TheIndexingExcludeSetting_ExcludesWithItsOwnReason()
    {
        using var temp = new TempDirectory();
        temp.Write("src/a.cs");
        temp.Write("src/Migrations/001.cs");
        temp.Write("notes.tmp");
        temp.Write("keep.cs");

        var entries = await ScanAsync(temp, new ScanOptions { ExcludePatterns = ["**/Migrations/**", "*.tmp"], MaxDegreeOfParallelism = 2 });

        AssertIncluded(entries, "src/a.cs");
        AssertIncluded(entries, "keep.cs");
        AssertExcluded(entries, "src/Migrations/001.cs", ExclusionReason.UserExclude);
        AssertExcluded(entries, "notes.tmp", ExclusionReason.UserExclude);
        Assert.Equal("indexing.exclude:2: *.tmp", entries["notes.tmp"].Decision.Detail);
    }

    [Theory]
    [InlineData("Foo.g.cs", ExclusionReason.GeneratedFile)]
    [InlineData("Foo.g.i.cs", ExclusionReason.GeneratedFile)]
    [InlineData("Form1.designer.cs", ExclusionReason.GeneratedFile)]
    [InlineData("Form1.Designer.cs", ExclusionReason.GeneratedFile)]
    [InlineData("Thing.generated.cs", ExclusionReason.GeneratedFile)]
    [InlineData("Form1.Designer.vb", ExclusionReason.GeneratedFile)]
    [InlineData("app.min.js", ExclusionReason.MinifiedFile)]
    [InlineData("site.MIN.css", ExclusionReason.MinifiedFile)]
    [InlineData("package-lock.json", ExclusionReason.LockFile)]
    [InlineData("yarn.lock", ExclusionReason.LockFile)]
    [InlineData("packages.lock.json", ExclusionReason.LockFile)]
    [InlineData("pnpm-lock.yaml", ExclusionReason.LockFile)]
    [InlineData("Cargo.lock", ExclusionReason.LockFile)]
    public async Task GeneratedMinifiedAndLockFileNames_AreExcludedWithTheirReason(string name, ExclusionReason reason)
    {
        using var temp = new TempDirectory();
        temp.Write($"src/{name}");
        temp.Write("src/real.cs");

        var entries = await ScanAsync(temp);

        AssertExcluded(entries, $"src/{name}", reason);
        AssertIncluded(entries, "src/real.cs");
    }

    [Fact]
    public async Task NamesThatOnlyLookGenerated_AreIncluded()
    {
        using var temp = new TempDirectory();
        temp.Write("g.cs");
        temp.Write("designer.cs");
        temp.Write("app.min.json");
        temp.Write("lock.txt");

        var entries = await ScanAsync(temp);

        Assert.All(entries.Values, e => Assert.True(e.Decision.IsIncluded, e.RelativePath));
    }

    [Theory]
    [InlineData("//------------------------------------------------------------------------------\n// <auto-generated>\n//     This code was generated by a tool.\n// </auto-generated>\n", "<auto-generated")]
    [InlineData("// @generated by protoc\nexport const a = 1;\n", "@generated")]
    [InlineData("# <AUTO-GENERATED> do not edit\n", "<auto-generated")]
    [InlineData("// Code generated by mockgen. DO NOT EDIT.\npackage mocks\n", "Code generated ... DO NOT EDIT")]
    public async Task AnAutoGeneratedHeader_ExcludesTheFile(string header, string marker)
    {
        using var temp = new TempDirectory();
        temp.Write("src/Thing.cs", header + "class Thing {}\n");

        var entries = await ScanAsync(temp);

        AssertExcluded(entries, "src/Thing.cs", ExclusionReason.GeneratedHeader);
        Assert.Equal(marker, entries["src/Thing.cs"].Decision.Detail, ignoreCase: true);
    }

    [Fact]
    public async Task AGeneratedMarkerBeyondTheFirst2KB_IsNotAHeader()
    {
        using var temp = new TempDirectory();
        temp.Write("late.cs", new string('/', ContentInspector.HeaderProbeBytes) + "\n// <auto-generated>\nclass C {}\n");
        temp.Write("early.cs", new string('/', ContentInspector.HeaderProbeBytes - 40) + "\n// <auto-generated>\nclass C {}\n");

        var entries = await ScanAsync(temp);

        AssertIncluded(entries, "late.cs");
        AssertExcluded(entries, "early.cs", ExclusionReason.GeneratedHeader);
    }

    [Fact]
    public async Task ANulByteInTheFirst8KB_MakesTheFileBinary_ButOneBeyondItDoesNot()
    {
        using var temp = new TempDirectory();
        temp.WriteBytes("image.dat", [.. "header"u8.ToArray(), 0, 1, 2, 3]);
        var nearEnd = new byte[ContentInspector.BinaryProbeBytes];
        Array.Fill(nearEnd, (byte)'a');
        nearEnd[^1] = 0;
        temp.WriteBytes("last-byte.bin", nearEnd);
        var late = new byte[ContentInspector.BinaryProbeBytes + 100];
        Array.Fill(late, (byte)'a');
        late[ContentInspector.BinaryProbeBytes] = 0;
        temp.WriteBytes("late-nul.txt", late);

        var entries = await ScanAsync(temp);

        AssertExcluded(entries, "image.dat", ExclusionReason.Binary);
        AssertExcluded(entries, "last-byte.bin", ExclusionReason.Binary);
        AssertIncluded(entries, "late-nul.txt");
    }

    [Fact]
    public async Task AUtf16FileWithAByteOrderMark_IsTextDespiteItsNulBytes()
    {
        using var temp = new TempDirectory();
        temp.WriteBytes("utf16le.txt", [.. System.Text.Encoding.Unicode.GetPreamble(), .. System.Text.Encoding.Unicode.GetBytes("hello world")]);
        temp.WriteBytes("utf16be.txt", [.. System.Text.Encoding.BigEndianUnicode.GetPreamble(), .. System.Text.Encoding.BigEndianUnicode.GetBytes("hello world")]);
        temp.WriteBytes("utf16-generated.cs", [.. System.Text.Encoding.Unicode.GetPreamble(), .. System.Text.Encoding.Unicode.GetBytes("// <auto-generated>\nclass C {}")]);
        temp.WriteBytes("no-bom.txt", System.Text.Encoding.Unicode.GetBytes("hello world"));

        var entries = await ScanAsync(temp);

        AssertIncluded(entries, "utf16le.txt");
        AssertIncluded(entries, "utf16be.txt");
        AssertExcluded(entries, "utf16-generated.cs", ExclusionReason.GeneratedHeader);
        AssertExcluded(entries, "no-bom.txt", ExclusionReason.Binary);
    }

    [Fact]
    public async Task TheSizeCap_ExcludesLargerFiles_AndKeepsFilesAtTheCap()
    {
        using var temp = new TempDirectory();
        temp.WriteBytes("exact.txt", Enumerable.Repeat((byte)'x', 1000).ToArray());
        temp.WriteBytes("over.txt", Enumerable.Repeat((byte)'x', 1001).ToArray());

        var entries = await ScanAsync(temp, new ScanOptions { MaxFileBytes = 1000, MaxDegreeOfParallelism = 2 });

        AssertIncluded(entries, "exact.txt");
        AssertExcluded(entries, "over.txt", ExclusionReason.TooLarge);
        Assert.Contains("1001", entries["over.txt"].Decision.Detail, StringComparison.Ordinal);
        Assert.Equal(1001, entries["over.txt"].Size);
    }

    [Fact]
    public async Task TheDefaultSizeCap_Is1MB()
    {
        using var temp = new TempDirectory();
        temp.WriteBytes("ok.txt", Enumerable.Repeat((byte)'x', 1024 * 1024).ToArray());
        temp.WriteBytes("big.txt", Enumerable.Repeat((byte)'x', (1024 * 1024) + 1).ToArray());

        var entries = await ScanAsync(temp);

        AssertIncluded(entries, "ok.txt");
        AssertExcluded(entries, "big.txt", ExclusionReason.TooLarge);
    }

    [Theory]
    [InlineData(".env")]
    [InlineData(".env.local")]
    [InlineData(".env.production")]
    [InlineData("cert.pfx")]
    [InlineData("server.PEM")]
    [InlineData("signing.key")]
    [InlineData("store.p12")]
    [InlineData("secrets.json")]
    [InlineData("appsettings.Development.json")]
    [InlineData("appsettings.Production.json")]
    [InlineData("site.publishsettings")]
    [InlineData("id_rsa")]
    [InlineData("id_ed25519")]
    [InlineData("id_ecdsa")]
    [InlineData("id_dsa")]
    [InlineData("putty.ppk")]
    [InlineData("release.keystore")]
    public async Task SecretBearingFiles_AreExcludedAndReportedAsSuch(string name)
    {
        using var temp = new TempDirectory();
        temp.Write($"config/{name}", "super secret");
        temp.Write("config/readme.md");

        var entries = await ScanAsync(temp);

        AssertExcluded(entries, $"config/{name}", ExclusionReason.SecretBearing);
        Assert.True(entries[$"config/{name}"].Decision.IsSecretBearing);
        Assert.False(string.IsNullOrEmpty(entries[$"config/{name}"].Decision.Detail));
        AssertIncluded(entries, "config/readme.md");
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("environment.md")]
    [InlineData("monkey.cs")]
    [InlineData("id_rsa.pub")]
    [InlineData("keys.txt")]
    public async Task NamesNextToTheSecretList_AreNotSecretBearing(string name)
    {
        using var temp = new TempDirectory();
        temp.Write(name);

        var entries = await ScanAsync(temp);

        AssertIncluded(entries, name);
    }

    [Fact]
    public async Task ASecretFile_StaysListedAsSecret_EvenWhenGitIgnored()
    {
        using var temp = new TempDirectory();
        temp.Write(".gitignore", ".env\n*.pem\n");
        temp.Write(".env", "KEY=1");
        temp.Write("tls/server.pem", "-----BEGIN");

        var entries = await ScanAsync(temp);

        AssertExcluded(entries, ".env", ExclusionReason.SecretBearing);
        AssertExcluded(entries, "tls/server.pem", ExclusionReason.SecretBearing);
    }

    [Fact]
    public async Task ExcludedFiles_AreNeverOpened()
    {
        using var temp = new TempDirectory();
        var locked = new[]
        {
            temp.Write("secrets.json", "x"),
            temp.Write("app.min.js", "x"),
            temp.Write("Foo.g.cs", "x"),
            temp.Write("package-lock.json", "{}"),
            temp.Write("debug.log", "x"),
        };
        temp.Write(".gitignore", "*.log\n");
        temp.Write("ok.cs", "class C {}");

        // Holding each file open without sharing makes any attempt to read it fail, which would show as Unreadable.
        var handles = locked.Select(path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)).ToList();
        try
        {
            var entries = await ScanAsync(temp);

            AssertExcluded(entries, "secrets.json", ExclusionReason.SecretBearing);
            AssertExcluded(entries, "app.min.js", ExclusionReason.MinifiedFile);
            AssertExcluded(entries, "Foo.g.cs", ExclusionReason.GeneratedFile);
            AssertExcluded(entries, "package-lock.json", ExclusionReason.LockFile);
            AssertExcluded(entries, "debug.log", ExclusionReason.GitIgnore);
            AssertIncluded(entries, "ok.cs");
        }
        finally
        {
            handles.ForEach(h => h.Dispose());
        }
    }

    [Fact]
    public async Task AFileThatCannotBeRead_IsReportedUnreadable()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("locked.cs", "class C {}");
        using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var entries = await ScanAsync(temp);

        AssertExcluded(entries, "locked.cs", ExclusionReason.Unreadable);
    }

    [Fact]
    public async Task ADirectoryJunction_IsNeverFollowed()
    {
        using var outside = new TempDirectory();
        outside.Write("secret-outside.cs");
        using var temp = new TempDirectory();
        temp.Write("real.cs");
        if (!TryCreateJunction(temp.File("link"), outside.Path))
        {
            // Junctions need no privilege on NTFS; skip quietly where the file system refuses them.
            return;
        }

        var entries = await ScanAsync(temp);

        AssertIncluded(entries, "real.cs");
        AssertExcluded(entries, "link", ExclusionReason.ReparsePoint);
        Assert.DoesNotContain(entries.Keys, k => k.Contains("secret-outside", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASubfolderWithItsOwnGit_IsAnotherRepository_AndNotEntered()
    {
        using var temp = new TempDirectory();
        temp.Write("keep.cs");
        temp.Write("vendor/lib/.git/HEAD", "ref: refs/heads/main");
        temp.Write("vendor/lib/code.cs");
        temp.Write("sub/.git", "gitdir: ../.git/modules/sub");
        temp.Write("sub/code.cs");

        var entries = await ScanAsync(temp);

        AssertIncluded(entries, "keep.cs");
        AssertExcluded(entries, "vendor/lib", ExclusionReason.NestedRepository);
        AssertExcluded(entries, "sub", ExclusionReason.NestedRepository);
        Assert.DoesNotContain(entries.Keys, k => k.EndsWith("code.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheRootItself_MayHaveAGit_AndIsStillScanned()
    {
        using var temp = new TempDirectory();
        temp.Write(".git/HEAD", "ref: refs/heads/main");
        temp.Write("code.cs");

        var entries = await ScanAsync(temp);

        AssertIncluded(entries, "code.cs");
        AssertExcluded(entries, ".git", ExclusionReason.BuiltIn);
    }

    [Fact]
    public async Task Entries_AreSortedWhenListed_AndSequentialScansAreDeterministic()
    {
        using var temp = new TempDirectory();
        foreach (var name in new[] { "b/two.cs", "a/one.cs", "c.cs", "b/three.cs" })
        {
            temp.Write(name);
        }

        var scanner = new RepositoryScanner(new ScanOptions { MaxDegreeOfParallelism = 1 });
        var streamed = new List<string>();
        await foreach (var entry in scanner.ScanAsync(temp.Path))
        {
            streamed.Add(entry.RelativePath);
        }

        var listed = (await scanner.ScanToListAsync(temp.Path)).Select(e => e.RelativePath).ToList();
        Assert.Equal(["a/one.cs", "b/three.cs", "b/two.cs", "c.cs"], listed);

        // One walker and one worker: a directory's files come first, then its subfolders, each in name order.
        Assert.Equal(["c.cs", "a/one.cs", "b/three.cs", "b/two.cs"], streamed);
    }

    [Fact]
    public async Task Enumeration_IsLazy_StopsEarly_AndHonoursCancellation()
    {
        using var temp = new TempDirectory();
        for (var i = 0; i < 40; i++)
        {
            for (var j = 0; j < 50; j++)
            {
                temp.Write($"d{i:D2}/f{j:D2}.cs", "class C {}");
            }
        }

        var scanner = new RepositoryScanner(new ScanOptions { MaxDegreeOfParallelism = 2 });

        // Taking a few entries and leaving the loop must end the scan promptly.
        var taken = 0;
        var stopwatch = Stopwatch.StartNew();
        await foreach (var entry in scanner.ScanAsync(temp.Path))
        {
            Assert.True(entry.Decision.IsIncluded);
            if (++taken == 5)
            {
                break;
            }
        }

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));

        // A cancelled token stops the scan with the usual exception.
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var entry in scanner.ScanAsync(temp.Path, cancellation.Token))
            {
                Assert.NotNull(entry);
                await cancellation.CancelAsync();
            }
        });
    }

    [Fact]
    public async Task AMissingRoot_Throws()
    {
        var scanner = new RepositoryScanner();
        var missing = Path.Combine(Path.GetTempPath(), "pillion-no-such-folder-" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => scanner.ScanToListAsync(missing));
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Pillion.Common.Tests;

public class PillionPathsTests
{
    [Fact]
    public void DefaultRoot_IsPillionProgrammerUnderLocalAppData()
    {
        var paths = new PillionPaths();

        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PillionProgrammer");
        Assert.Equal(expected, paths.Root);
    }

    [Fact]
    public void Properties_AreRelativeToTheOverriddenRoot()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "root");
        var paths = new PillionPaths(root);

        Assert.Equal(root, paths.Root);
        Assert.Equal(Path.Combine(root, "settings.json"), paths.SettingsFile);
        Assert.Equal(Path.Combine(root, "rules.md"), paths.RulesFile);
        Assert.Equal(Path.Combine(root, "prompts"), paths.PromptsDirectory);
        Assert.Equal(Path.Combine(root, "hooks.json"), paths.HooksFile);
        Assert.Equal(Path.Combine(root, "trust.json"), paths.TrustFile);
        Assert.Equal(Path.Combine(root, "logs"), paths.LogsDirectory);
        Assert.Equal(Path.Combine(root, "crash"), paths.CrashDirectory);
        Assert.Equal(Path.Combine(root, "models"), paths.ModelsDirectory);
        Assert.Equal(Path.Combine(root, "index"), paths.IndexDirectory);
        Assert.Equal(Path.Combine(root, "conversations.db"), paths.ConversationsDatabase);
        Assert.Equal(Path.Combine(root, "audit"), paths.AuditDirectory);
        Assert.Equal(Path.Combine(root, "checkpoints"), paths.CheckpointsDirectory);
        Assert.Equal(Path.Combine(root, "browser-profiles"), paths.BrowserProfilesDirectory);
    }

    [Fact]
    public void EnsureCreated_CreatesTheRootOnceAndReportsIt()
    {
        using var temp = new TempDirectory();
        var paths = temp.NewPaths();

        Assert.True(paths.EnsureCreated());
        Assert.True(Directory.Exists(paths.Root));
        Assert.False(paths.EnsureCreated());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void EnsureCreated_RestrictsAccessToTheCurrentUserAndSystem()
    {
        using var temp = new TempDirectory();
        var paths = temp.NewPaths();
        paths.EnsureCreated();

        var security = new DirectoryInfo(paths.Root).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected, "Inheritance from the parent should be removed.");

        var me = WindowsIdentity.GetCurrent().User!;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();

        Assert.All(rules, rule => Assert.True(
            rule.IdentityReference.Equals(me) || rule.IdentityReference.Equals(system),
            $"Unexpected identity {rule.IdentityReference}"));
        Assert.Contains(rules, r => r.IdentityReference.Equals(me) && r.FileSystemRights.HasFlag(FileSystemRights.FullControl));
        Assert.Contains(rules, r => r.IdentityReference.Equals(system) && r.FileSystemRights.HasFlag(FileSystemRights.FullControl));
    }
}

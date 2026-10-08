using Microsoft.Extensions.Logging;
using Pillion.Common.Settings;

namespace Pillion.Common.Tests;

public class EffectiveSettingsTests
{
    [Fact]
    public void Resolve_WithNoPolicies_ReturnsUserSettingsUnchanged()
    {
        var user = new PillionSettings();
        user.Indexing.Exclude = ["**/obj/**"];
        user.Privacy.LocalOnly = true;

        var effective = EffectiveSettings.Resolve(user, []);

        Assert.True(effective.LocalOnly);
        Assert.Equal(["**/obj/**"], effective.Exclude);
        Assert.Empty(effective.DisabledTools);
        Assert.Empty(effective.DenyCommands);
    }

    [Fact]
    public void Resolve_LocalOnly_CanOnlyBeTurnedOn()
    {
        var off = new PillionSettings();
        var on = new PillionSettings();
        on.Privacy.LocalOnly = true;
        var restrictive = RepositoryPolicy.Parse("""{ "localOnly": true }""");
        var permissive = RepositoryPolicy.Parse("""{ "localOnly": false }""");

        Assert.True(EffectiveSettings.Resolve(off, [restrictive]).LocalOnly);
        Assert.False(EffectiveSettings.Resolve(off, [permissive]).LocalOnly);
        Assert.True(EffectiveSettings.Resolve(on, [permissive]).LocalOnly);
        Assert.True(EffectiveSettings.Resolve(on, [permissive, restrictive]).LocalOnly);
    }

    [Fact]
    public void Resolve_ListKeys_AreUnionedAcrossUserAndAllPolicies()
    {
        var user = new PillionSettings();
        user.Indexing.Exclude = ["**/bin/**", "secrets/**"];
        var a = RepositoryPolicy.Parse("""{ "disabledTools": ["runCommand", "browser"], "exclude": ["secrets/**", "*.pem"], "denyCommands": ["rm", "format"] }""");
        var b = RepositoryPolicy.Parse("""{ "disabledTools": ["browser", "deleteFile"], "exclude": ["vendor/**"], "denyCommands": ["format", "shutdown"] }""");

        var effective = EffectiveSettings.Resolve(user, [a, b]);

        Assert.Equal(["**/bin/**", "secrets/**", "*.pem", "vendor/**"], effective.Exclude);
        Assert.Equal(["runCommand", "browser", "deleteFile"], effective.DisabledTools);
        Assert.Equal(["rm", "format", "shutdown"], effective.DenyCommands);
        Assert.Equal(effective.Exclude, effective.Settings.Indexing.Exclude);
    }

    [Fact]
    public void Resolve_DoesNotChangeTheUserSettingsObject()
    {
        var user = new PillionSettings();
        var policy = RepositoryPolicy.Parse("""{ "localOnly": true, "exclude": ["x/**"] }""");

        _ = EffectiveSettings.Resolve(user, [policy]);

        Assert.False(user.Privacy.LocalOnly);
        Assert.Empty(user.Indexing.Exclude);
    }

    [Fact]
    public void Resolve_KeepsEverythingElseFromTheUserSettings()
    {
        var user = new PillionSettings();
        user.Approvals.Mode = ApprovalMode.Autonomous;
        user.Providers.Add(new ProviderSettings { Id = "openai", Models = ["m"] });
        user.Logging.Level = LogLevel.Trace;

        var effective = EffectiveSettings.Resolve(user, [RepositoryPolicy.Parse("""{ "localOnly": true }""")]);

        Assert.Equal(ApprovalMode.Autonomous, effective.Settings.Approvals.Mode);
        Assert.Equal("openai", Assert.Single(effective.Settings.Providers).Id);
        Assert.Equal(LogLevel.Trace, effective.Settings.Logging.Level);
    }

    [Fact]
    public void Parse_IgnoresUnknownKeysWithAWarning_AndNeverHonoursThem()
    {
        var policy = RepositoryPolicy.Parse("""
            {
              "localOnly": true,
              "approvals": { "mode": "autonomous" },
              "cloudSwitches": { "agent": true },
              "providers": [],
              "disabledTools": ["x"]
            }
            """);

        Assert.True(policy.LocalOnly);
        Assert.Equal(["x"], policy.DisabledTools);
        Assert.Equal(3, policy.Warnings.Count);
        Assert.Contains(policy.Warnings, w => w.Contains("approvals", StringComparison.Ordinal));
        Assert.Contains(policy.Warnings, w => w.Contains("cloudSwitches", StringComparison.Ordinal));
        Assert.Contains(policy.Warnings, w => w.Contains("providers", StringComparison.Ordinal));

        var user = new PillionSettings();
        var effective = EffectiveSettings.Resolve(user, [policy]);
        Assert.Equal(ApprovalMode.Ask, effective.Settings.Approvals.Mode);
        Assert.False(effective.Settings.CloudSwitches.Agent);
    }

    [Fact]
    public void Parse_IgnoresValuesOfTheWrongTypeWithAWarning()
    {
        var policy = RepositoryPolicy.Parse("""{ "localOnly": "yes", "exclude": "all", "denyCommands": ["ok", 5, ""] }""");

        Assert.False(policy.LocalOnly);
        Assert.Empty(policy.Exclude);
        Assert.Equal(["ok"], policy.DenyCommands);
        Assert.Equal(4, policy.Warnings.Count);
    }

    [Fact]
    public void Parse_WarningsAreLogged()
    {
        var logger = new ListLogger();

        _ = RepositoryPolicy.Parse("""{ "surprise": 1 }""", logger, "repo/.pillion/policy.json");

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("surprise", entry.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{ nope")]
    public void Parse_RejectsDocumentsThatAreNotObjects(string json)
    {
        Assert.Throws<SettingsValidationException>(() => RepositoryPolicy.Parse(json));
    }

    [Fact]
    public void LoadFromRepository_ReadsThePolicyFileOrReturnsEmpty()
    {
        using var temp = new TempDirectory();
        Assert.Same(RepositoryPolicy.Empty, RepositoryPolicy.LoadFromRepository(temp.Path));

        Directory.CreateDirectory(Path.Combine(temp.Path, ".pillion"));
        File.WriteAllText(Path.Combine(temp.Path, ".pillion", "policy.json"), """{ "localOnly": true, "denyCommands": ["curl"] }""");

        var policy = RepositoryPolicy.LoadFromRepository(temp.Path);

        Assert.True(policy.LocalOnly);
        Assert.Equal(["curl"], policy.DenyCommands);
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}

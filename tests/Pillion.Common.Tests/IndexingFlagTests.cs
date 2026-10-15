using Pillion.Common.Settings;

namespace Pillion.Common.Tests;

public sealed class IndexingFlagTests
{
    [Fact]
    public void Indexing_IsOnByDefault_AndTheUserOrAPolicyCanSwitchItOff()
    {
        Assert.True(new FeatureSettings().IsEnabled(FeatureNames.Indexing));
        Assert.False(new FeatureSettings { Indexing = false }.IsEnabled("Indexing"));

        var effective = EffectiveSettings.Resolve(new PillionSettings(), [RepositoryPolicy.Parse("""{ "disabledFeatures": ["indexing"] }""")]);

        Assert.False(effective.IsFeatureEnabled(FeatureNames.Indexing));
        Assert.True(effective.IsFeatureEnabled(FeatureNames.LocalStore));
    }

    [Fact]
    public void RetentionDays_DefaultsToThirty()
    {
        Assert.Equal(30, new IndexingSettings().RetentionDays);
    }
}

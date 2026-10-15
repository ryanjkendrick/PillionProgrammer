using Pillion.Common.Settings;

namespace Pillion.Common.Tests;

public sealed class LocalStoreFlagTests
{
    [Fact]
    public void LocalStore_IsOnByDefault_AndTheUserOrAPolicyCanSwitchItOff()
    {
        Assert.True(new FeatureSettings().IsEnabled(FeatureNames.LocalStore));
        Assert.False(new FeatureSettings { LocalStore = false }.IsEnabled("LocalStore"));

        var effective = EffectiveSettings.Resolve(new PillionSettings(), [RepositoryPolicy.Parse("""{ "disabledFeatures": ["localStore"] }""")]);

        Assert.False(effective.IsFeatureEnabled(FeatureNames.LocalStore));
        Assert.True(effective.IsFeatureEnabled(FeatureNames.Chat));
    }
}

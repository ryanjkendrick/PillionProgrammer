using Pillion.Common.Settings;

namespace Pillion.Common.Tests;

public sealed class InlineChatFlagTests
{
    [Fact]
    public void InlineChat_IsOnByDefault_AndTheUserOrAPolicyCanSwitchItOff()
    {
        Assert.True(new FeatureSettings().IsEnabled(FeatureNames.InlineChat));
        Assert.False(new FeatureSettings { InlineChat = false }.IsEnabled("InlineChat"));

        var effective = EffectiveSettings.Resolve(new PillionSettings(), [RepositoryPolicy.Parse("""{ "disabledFeatures": ["inlineChat"] }""")]);

        Assert.False(effective.IsFeatureEnabled(FeatureNames.InlineChat));
        Assert.True(effective.IsFeatureEnabled(FeatureNames.Autocomplete));
    }
}

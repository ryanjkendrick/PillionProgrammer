using Pillion.Common.Settings;

namespace Pillion.Common.Tests;

public sealed class MentionsFlagTests
{
    [Fact]
    public void Mentions_IsOnByDefault_AndTheUserOrAPolicyCanSwitchItOff()
    {
        Assert.True(new FeatureSettings().IsEnabled(FeatureNames.Mentions));
        Assert.False(new FeatureSettings { Mentions = false }.IsEnabled("Mentions"));

        var effective = EffectiveSettings.Resolve(new PillionSettings(), [RepositoryPolicy.Parse("""{ "disabledFeatures": ["mentions"] }""")]);

        Assert.False(effective.IsFeatureEnabled(FeatureNames.Mentions));
        Assert.True(effective.IsFeatureEnabled(FeatureNames.OutboundLogView));
        Assert.True(effective.IsFeatureEnabled(FeatureNames.Chat));
    }

    [Fact]
    public void OutboundLogView_IsOnByDefault_AndTheUserOrAPolicyCanSwitchItOff()
    {
        Assert.True(new FeatureSettings().IsEnabled(FeatureNames.OutboundLogView));
        Assert.False(new FeatureSettings { OutboundLogView = false }.IsEnabled("outboundlogview"));

        var effective = EffectiveSettings.Resolve(new PillionSettings(), [RepositoryPolicy.Parse("""{ "disabledFeatures": ["outboundLogView"] }""")]);

        Assert.False(effective.IsFeatureEnabled(FeatureNames.OutboundLogView));
        Assert.True(effective.IsFeatureEnabled(FeatureNames.Mentions));
    }
}

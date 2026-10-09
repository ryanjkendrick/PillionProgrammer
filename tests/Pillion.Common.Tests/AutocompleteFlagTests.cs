using Pillion.Common.Settings;

namespace Pillion.Common.Tests;

public sealed class AutocompleteFlagTests
{
    [Fact]
    public void Autocomplete_IsOnByDefault_AndTheUserOrAPolicyCanSwitchItOff()
    {
        Assert.True(new FeatureSettings().IsEnabled(FeatureNames.Autocomplete));
        Assert.False(new FeatureSettings { Autocomplete = false }.IsEnabled("Autocomplete"));

        var effective = EffectiveSettings.Resolve(new PillionSettings(), [RepositoryPolicy.Parse("""{ "disabledFeatures": ["autocomplete"] }""")]);

        Assert.False(effective.IsFeatureEnabled(FeatureNames.Autocomplete));
        Assert.True(effective.IsFeatureEnabled(FeatureNames.ModelGateway));
    }
}

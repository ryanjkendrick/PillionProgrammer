namespace Pillion.Common.Tests;

public class CommonAssemblyMarkerTests
{
    [Fact]
    public void AssemblyName_IsPillionCommon()
    {
        Assert.Equal("Pillion.Common", CommonAssemblyMarker.AssemblyName);
    }
}

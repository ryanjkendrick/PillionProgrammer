namespace Pillion.ModelGateway.Tests;

public class ModelGatewayAssemblyMarkerTests
{
    [Fact]
    public void AssemblyName_IsPillionModelGateway()
    {
        Assert.Equal("Pillion.ModelGateway", ModelGatewayAssemblyMarker.AssemblyName);
    }
}

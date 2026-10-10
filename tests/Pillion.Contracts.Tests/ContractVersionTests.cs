namespace Pillion.Contracts.Tests;

public class ContractVersionTests
{
    // The current version (1.2) is asserted in ContractV12Tests.
    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 7)]
    [InlineData(1, 42)]
    public void IsCompatible_SameMajor_ReturnsTrue(int major, int minor)
    {
        Assert.True(ContractVersion.IsCompatible(major, minor));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 9)]
    [InlineData(2, 0)]
    [InlineData(2, 5)]
    public void IsCompatible_DifferentMajor_ReturnsFalse(int major, int minor)
    {
        Assert.False(ContractVersion.IsCompatible(major, minor));
    }
}

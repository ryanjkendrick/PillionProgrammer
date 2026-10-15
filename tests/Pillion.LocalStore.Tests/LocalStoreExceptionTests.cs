using Pillion.Contracts;

namespace Pillion.LocalStore.Tests;

/// <summary>Contract 1.3: Local Store failures carry <see cref="PillionErrorCode.StorageUnavailable"/>, except a flag that is off.</summary>
public sealed class LocalStoreExceptionTests
{
    public static TheoryData<LocalStoreErrorKind> StorageKinds =>
        new(Enum.GetValues<LocalStoreErrorKind>().Where(k => k != LocalStoreErrorKind.FeatureDisabled));

    [Theory]
    [MemberData(nameof(StorageKinds))]
    public void AStoreFailure_IsStorageUnavailable(LocalStoreErrorKind kind)
    {
        var error = new LocalStoreException(kind, "The store failed.", "Retry.", @"C:\data\conversations.db");

        Assert.Equal(PillionErrorCode.StorageUnavailable, error.Error.Code);
        Assert.Equal(PillionErrorCode.StorageUnavailable, LocalStoreException.CodeFor(kind));
        Assert.Equal(kind, error.Kind);
        Assert.Equal("The store failed.", error.Error.Message);
    }

    [Fact]
    public void TheFlagBeingOff_IsPolicyDenied_LikeEveryOtherFlag()
    {
        var error = new LocalStoreException(LocalStoreErrorKind.FeatureDisabled, "Turned off (feature localStore).");

        Assert.Equal(PillionErrorCode.PolicyDenied, error.Error.Code);
    }
}

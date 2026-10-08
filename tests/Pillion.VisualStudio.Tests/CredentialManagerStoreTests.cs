using System;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Tests;

/// <summary>Round trips through the real Windows Credential Manager under a unique test target, cleaned up afterwards.</summary>
public sealed class CredentialManagerStoreTests : IDisposable
{
    private readonly CredentialManagerStore _store = new();
    private readonly string _id = "test-" + Guid.NewGuid().ToString("N");

    public void Dispose() => _store.Delete(_id);

    [Fact]
    public void WriteExistsDelete_RoundTripsUnderTheHostsTargetName()
    {
        Assert.Equal("PillionProgrammer/" + _id, CredentialManagerStore.TargetFor(_id));
        Assert.False(_store.Exists(_id));
        Assert.Null(CredentialManagerStore.ReadForTest(_id));

        _store.Write(_id, "sk-first-value é中");
        Assert.True(_store.Exists(_id));
        Assert.Equal("sk-first-value é中", CredentialManagerStore.ReadForTest(_id));

        _store.Write(_id, "sk-second-value");
        Assert.Equal("sk-second-value", CredentialManagerStore.ReadForTest(_id));

        Assert.True(_store.Delete(_id));
        Assert.False(_store.Exists(_id));
        Assert.False(_store.Delete(_id));
    }

    [Fact]
    public void Write_AcceptsTheLargestKeyThatFits_AndRejectsLongerOnesWithoutEchoingThem()
    {
        var largest = new string('k', CredentialManagerStore.MaxBlobBytes / 2);
        _store.Write(_id, largest);
        Assert.Equal(largest, CredentialManagerStore.ReadForTest(_id));

        var tooLong = new string('x', (CredentialManagerStore.MaxBlobBytes / 2) + 1);
        var ex = Assert.Throws<ArgumentException>(() => _store.Write(_id, tooLong));
        Assert.DoesNotContain("xxxx", ex.Message);
        Assert.Throws<ArgumentException>(() => _store.Write(_id, string.Empty));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("slash/name")]
    [InlineData("star*")]
    public void InvalidProviderIds_AreRejected(string id)
    {
        Assert.False(CredentialManagerStore.IsValidName(id));
        Assert.Throws<ArgumentException>(() => _store.Exists(id));
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("azure-openai.eu_1")]
    public void ProviderIds_FollowTheHostsSecretNameRule(string id) => Assert.True(CredentialManagerStore.IsValidName(id));
}

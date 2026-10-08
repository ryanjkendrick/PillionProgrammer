using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading.Tasks;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Tests;

public class PipeVerificationTests
{
    [Fact]
    public async Task VerifyServer_AcceptsThePipeServedByTheExpectedProcess()
    {
        using var current = Process.GetCurrentProcess();
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            HostProcessLauncher.VerifyServer(client, current.Id);
        }
    }

    [Fact]
    public async Task VerifyServer_TreatsASquattedPipeAsARetryableCrash()
    {
        using var current = Process.GetCurrentProcess();
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            // The pipe is served by this (test) process, not by the "host" we pretend to have launched.
            var ex = Assert.Throws<HostLaunchException>(() => HostProcessLauncher.VerifyServer(client, current.Id + 1));

            Assert.True(ex.IsRetryable);
            Assert.Contains("not by the host that was started", ex.Message);
        }
    }

    [Fact]
    public async Task VerifyServer_AcceptsAnAdministratorsOwnedPipeWhenTheTokenIsElevated()
    {
        // An elevated token owns what it creates as BUILTIN\Administrators, not as the user (the hosted CI runner).
        using var current = Process.GetCurrentProcess();
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            HostProcessLauncher.VerifyServer(client, current.Id, ElevatedAdministrator, _ => Administrators);
        }
    }

    [Fact]
    public async Task VerifyServer_AcceptsAPipeOwnedByTheUser()
    {
        using var current = Process.GetCurrentProcess();
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            HostProcessLauncher.VerifyServer(client, current.Id, ElevatedAdministrator, _ => UserSid);
            HostProcessLauncher.VerifyServer(client, current.Id, NormalUser, _ => UserSid);
        }
    }

    [Fact]
    public async Task VerifyServer_RefusesAnAdministratorsOwnedPipeWhenTheTokenIsNotElevated()
    {
        using var current = Process.GetCurrentProcess();
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var ex = Assert.Throws<HostLaunchException>(() => HostProcessLauncher.VerifyServer(client, current.Id, NormalUser, _ => Administrators));

            Assert.Contains("owned by another user", ex.Message);
        }
    }

    [Fact]
    public async Task VerifyServer_RefusesAForeignOwnerWhateverTheTokenIs()
    {
        using var current = Process.GetCurrentProcess();
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            foreach (var identity in new[] { NormalUser, ElevatedAdministrator })
            {
                var ex = Assert.Throws<HostLaunchException>(() => HostProcessLauncher.VerifyServer(client, current.Id, identity, _ => OtherUser));

                Assert.Contains("owned by another user", ex.Message);
                Assert.Contains(OtherUser.Value, ex.Message);
            }
        }
    }

    [Fact]
    public async Task VerifyServer_RefusesAPipeWithoutAnOwner()
    {
        using var current = Process.GetCurrentProcess();
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            Assert.Throws<HostLaunchException>(() => HostProcessLauncher.VerifyServer(client, current.Id, ElevatedAdministrator, _ => null));
        }
    }

    [Fact]
    public async Task VerifyServer_ChecksTheServerProcessBeforeTheOwner()
    {
        using var current = Process.GetCurrentProcess();
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var ex = Assert.Throws<HostLaunchException>(() => HostProcessLauncher.VerifyServer(client, current.Id + 1, ElevatedAdministrator, _ => Administrators));

            Assert.Contains("not by the host that was started", ex.Message);
        }
    }

    [Fact]
    public void IsTrustedOwner_IsTrueOnlyForTheUserOrTheTokenOwner()
    {
        Assert.True(PipeIdentity.IsTrustedOwner(UserSid, NormalUser));
        Assert.True(PipeIdentity.IsTrustedOwner(UserSid, ElevatedAdministrator));
        Assert.True(PipeIdentity.IsTrustedOwner(Administrators, ElevatedAdministrator));
        Assert.False(PipeIdentity.IsTrustedOwner(Administrators, NormalUser));
        Assert.False(PipeIdentity.IsTrustedOwner(OtherUser, ElevatedAdministrator));
        Assert.False(PipeIdentity.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), ElevatedAdministrator));
        Assert.False(PipeIdentity.IsTrustedOwner(null, NormalUser));
    }

    [Fact]
    public void TheRealIdentity_OwnsWhatItCreates()
    {
        // Whatever the token is (normal or elevated), the owner it assigns to new objects must be trusted.
        var identity = PipeIdentity.Current;

        Assert.NotNull(identity.User);
        Assert.NotNull(identity.TokenOwner);
        Assert.True(PipeIdentity.IsTrustedOwner(identity.TokenOwner, identity));
        Assert.True(PipeIdentity.IsTrustedOwner(identity.User, identity));
    }

    private static SecurityIdentifier Administrators { get; } = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private static SecurityIdentifier UserSid { get; } = new("S-1-5-21-1000000001-1000000002-1000000003-1001");

    private static SecurityIdentifier OtherUser { get; } = new("S-1-5-21-1000000001-1000000002-1000000003-1002");

    private static FakeIdentity NormalUser { get; } = new(UserSid, UserSid);

    private static FakeIdentity ElevatedAdministrator { get; } = new(UserSid, Administrators);

    private static async Task<(NamedPipeServerStream Server, NamedPipeClientStream Client)> ConnectAsync()
    {
        var name = LaunchProtocol.CreatePipeName(12345);
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var waiting = server.WaitForConnectionAsync();
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        client.Connect(5000);
        await waiting;
        return (server, client);
    }
}

internal sealed class FakeIdentity(SecurityIdentifier? user, SecurityIdentifier? tokenOwner) : IPipeIdentity
{
    public SecurityIdentifier? User { get; } = user;

    public SecurityIdentifier? TokenOwner { get; } = tokenOwner;
}

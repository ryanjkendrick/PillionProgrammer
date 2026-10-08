using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Contracts;
using Pillion.Host.Lifecycle;

namespace Pillion.Host.Tests;

public class HostStateMachineTests
{
    [Fact]
    public void NewMachine_IsStarting()
    {
        Assert.Equal(HostState.Starting, new HostStateMachine(NullLogger<HostStateMachine>.Instance).Current);
    }

    [Theory]
    [InlineData(HostState.Starting, HostState.Ready)]
    [InlineData(HostState.Starting, HostState.Failed)]
    [InlineData(HostState.Ready, HostState.Degraded)]
    [InlineData(HostState.Ready, HostState.Restarting)]
    [InlineData(HostState.Ready, HostState.Failed)]
    [InlineData(HostState.Degraded, HostState.Ready)]
    [InlineData(HostState.Degraded, HostState.Restarting)]
    [InlineData(HostState.Degraded, HostState.Failed)]
    [InlineData(HostState.Restarting, HostState.Starting)]
    [InlineData(HostState.Restarting, HostState.Ready)]
    [InlineData(HostState.Restarting, HostState.Failed)]
    public void IsAllowed_DocumentedTransitions(HostState from, HostState to)
    {
        Assert.True(HostStateMachine.IsAllowed(from, to));
    }

    [Theory]
    [InlineData(HostState.Starting, HostState.Degraded)]
    [InlineData(HostState.Starting, HostState.Restarting)]
    [InlineData(HostState.Ready, HostState.Starting)]
    [InlineData(HostState.Failed, HostState.Ready)]
    [InlineData(HostState.Failed, HostState.Starting)]
    public void IsAllowed_OtherTransitions_AreRejected(HostState from, HostState to)
    {
        Assert.False(HostStateMachine.IsAllowed(from, to));
    }

    [Fact]
    public void TryTransition_RaisesStateChangedOnlyForRealChanges()
    {
        var machine = new HostStateMachine(NullLogger<HostStateMachine>.Instance);
        var changes = new List<(HostState, HostState)>();
        machine.StateChanged += (_, e) => changes.Add((e.Previous, e.Current));

        Assert.True(machine.TryTransition(HostState.Ready, "listening"));
        Assert.True(machine.TryTransition(HostState.Ready, "again"));
        Assert.False(machine.TryTransition(HostState.Starting, "not allowed"));
        Assert.True(machine.TryTransition(HostState.Degraded, "provider down"));

        Assert.Equal([(HostState.Starting, HostState.Ready), (HostState.Ready, HostState.Degraded)], changes);
        Assert.Equal(HostState.Degraded, machine.Current);
    }

    [Fact]
    public void Failed_IsTerminal()
    {
        var machine = new HostStateMachine(NullLogger<HostStateMachine>.Instance);
        Assert.True(machine.TryTransition(HostState.Failed, "boom"));

        Assert.False(machine.TryTransition(HostState.Ready, "recover"));
        Assert.Equal(HostState.Failed, machine.Current);
    }
}

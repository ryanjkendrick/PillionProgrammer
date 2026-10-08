using System.Net.NetworkInformation;

namespace Pillion.Common.Connectivity;

/// <summary>Raises an event when the machine's network availability or addresses change.</summary>
public interface INetworkChangeSource
{
    /// <summary>
    /// Raised on a thread-pool thread when the network changed. A single adapter change usually raises several
    /// events, so subscribers should debounce.
    /// </summary>
    event EventHandler? Changed;
}

/// <summary>
/// The <see cref="INetworkChangeSource"/> backed by <see cref="NetworkChange.NetworkAvailabilityChanged"/> and
/// <see cref="NetworkChange.NetworkAddressChanged"/>. It only hooks the operating system events while somebody is
/// subscribed.
/// </summary>
public sealed class SystemNetworkChangeSource : INetworkChangeSource
{
    private readonly object _gate = new();
    private EventHandler? _handlers;

    /// <inheritdoc/>
    public event EventHandler? Changed
    {
        add
        {
            lock (_gate)
            {
                if (_handlers is null)
                {
                    NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
                    NetworkChange.NetworkAddressChanged += OnAddressChanged;
                }

                _handlers += value;
            }
        }

        remove
        {
            lock (_gate)
            {
                _handlers -= value;
                if (_handlers is null)
                {
                    NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
                    NetworkChange.NetworkAddressChanged -= OnAddressChanged;
                }
            }
        }
    }

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Raise();

    private void OnAddressChanged(object? sender, EventArgs e) => Raise();

    private void Raise()
    {
        EventHandler? handlers;
        lock (_gate)
        {
            handlers = _handlers;
        }

        handlers?.Invoke(this, EventArgs.Empty);
    }
}

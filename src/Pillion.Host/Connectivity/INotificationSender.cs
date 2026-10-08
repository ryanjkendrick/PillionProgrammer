using Pillion.Host.Rpc;

namespace Pillion.Host.Connectivity;

/// <summary>Sends JSON-RPC notifications from the host to the connected Visual Studio extension.</summary>
internal interface INotificationSender
{
    /// <summary>Raised when an authenticated client connects (its handshake was accepted).</summary>
    event EventHandler? ClientConnected;

    /// <summary>Sends a notification with one positional argument to the connected client.</summary>
    /// <param name="method">The notification name.</param>
    /// <param name="argument">The single positional argument.</param>
    /// <returns><see langword="true"/> when the notification was written; <see langword="false"/> when no client is connected.</returns>
    Task<bool> TrySendAsync(string method, object? argument);
}

/// <summary>The <see cref="INotificationSender"/> that writes to the connection held by <see cref="VsixConnection"/>.</summary>
internal sealed class VsixNotificationSender : INotificationSender, IDisposable
{
    private readonly VsixConnection _connection;

    /// <summary>Initializes a new instance of the <see cref="VsixNotificationSender"/> class.</summary>
    /// <param name="connection">The host-to-VSIX connection holder.</param>
    public VsixNotificationSender(VsixConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _connection = connection;
        _connection.ConnectionChanged += OnConnectionChanged;
    }

    /// <inheritdoc/>
    public event EventHandler? ClientConnected;

    /// <inheritdoc/>
    public Task<bool> TrySendAsync(string method, object? argument) => _connection.TryNotifyAsync(method, argument);

    /// <inheritdoc/>
    public void Dispose() => _connection.ConnectionChanged -= OnConnectionChanged;

    private void OnConnectionChanged(object? sender, EventArgs e)
    {
        if (_connection.IsConnected)
        {
            ClientConnected?.Invoke(this, EventArgs.Empty);
        }
    }
}

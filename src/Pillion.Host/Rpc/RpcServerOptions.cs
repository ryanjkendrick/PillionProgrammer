namespace Pillion.Host.Rpc;

/// <summary>Timeouts for the RPC server. The defaults are the production values; tests shorten them.</summary>
internal sealed class RpcServerOptions
{
    /// <summary>
    /// Gets or sets how long a new connection may go without an accepted handshake before it is closed, so a stuck
    /// or hostile client cannot hold the single pipe instance. Default 10 seconds.
    /// </summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets how long a graceful stop waits for in-flight calls to finish before cancelling them by closing the
    /// connection. Default 1 second.
    /// </summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets how long after a rejected handshake the connection is closed if the rejection response has not
    /// been seen leaving the host by then. Default 2 seconds.
    /// </summary>
    public TimeSpan RejectionCloseDelay { get; set; } = TimeSpan.FromSeconds(2);
}

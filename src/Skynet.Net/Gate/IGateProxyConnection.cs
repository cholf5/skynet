namespace Skynet.Net;

/// <summary>
/// A single live outbound connection owned by a <see cref="GateProxy"/>. Implementations raise
/// <see cref="Disconnected"/> exactly once when the connection drops (remote close, transport
/// error, or EOF) so the owning <see cref="GateClient"/> can schedule a reconnect.
/// </summary>
public interface IGateProxyConnection : IAsyncDisposable
{
	string ConnectionId { get; }

	bool IsConnected { get; }

	event EventHandler<GateDisconnectedEventArgs>? Disconnected;

	/// <summary>
	/// Raised for each inbound frame delivered by the transport's receive pump. Transports
	/// without an inbound path (e.g. loopback fakes used by unit tests) may leave this event unraised.
	/// </summary>
	event EventHandler<GateFrameReceivedEventArgs>? FrameReceived;

	Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
}

public sealed class GateDisconnectedEventArgs : EventArgs
{
	public GateDisconnectedEventArgs(Exception? exception = null)
	{
		Exception = exception;
	}

	public Exception? Exception { get; }
}

public sealed class GateFrameReceivedEventArgs : EventArgs
{
	public GateFrameReceivedEventArgs(ReadOnlyMemory<byte> payload)
	{
		Payload = payload;
	}

	public ReadOnlyMemory<byte> Payload { get; }
}

/// <summary>
/// Payload for <see cref="GateClient.Connected"/>. Carries the freshly installed connection so
/// listeners can subscribe to inbound frames and run per-connection setup.
/// </summary>
public sealed class GateClientConnectedEventArgs : EventArgs
{
	public GateClientConnectedEventArgs(IGateProxyConnection connection)
	{
		Connection = connection ?? throw new ArgumentNullException(nameof(connection));
	}

	public IGateProxyConnection Connection { get; }
}

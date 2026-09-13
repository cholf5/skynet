namespace Skynet.Net;

/// <summary>
/// Lock-free hot-swappable handle to the current outbound connection. Senders read the
/// connection with <see cref="Volatile.Read"/> so a reconnect never blocks an in-flight send;
/// the connect loop swaps it atomically with <see cref="Interlocked.Exchange"/>.
/// </summary>
public sealed class GateProxy
{
	private IGateProxyConnection? _connection;

	public GateProxy(GateEndpoint endpoint)
	{
		Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
	}

	public GateEndpoint Endpoint { get; }

	public bool IsConnected => Volatile.Read(ref _connection)?.IsConnected == true;

	public string? ConnectionId => Volatile.Read(ref _connection)?.ConnectionId;

	/// <summary>
	/// Sends an opaque payload over the current connection. Throws
	/// <see cref="InvalidOperationException"/> when the Gate is disconnected; the caller decides
	/// whether to drop or buffer the message. Never blocks on a reconnect in progress.
	/// </summary>
	public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
	{
		var connection = Volatile.Read(ref _connection);
		if (connection is null || !connection.IsConnected)
		{
			throw new InvalidOperationException($"Gate proxy '{Endpoint.Name}' is disconnected.");
		}

		return connection.SendAsync(payload, cancellationToken);
	}

	internal IGateProxyConnection? CurrentConnection => Volatile.Read(ref _connection);

	internal IGateProxyConnection? ReplaceConnection(IGateProxyConnection? connection)
	{
		return Interlocked.Exchange(ref _connection, connection);
	}

	internal bool TryClearConnection(IGateProxyConnection connection)
	{
		return Interlocked.CompareExchange(ref _connection, null, connection) == connection;
	}
}

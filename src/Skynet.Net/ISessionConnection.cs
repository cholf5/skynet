namespace Skynet.Net;

/// <summary>
/// Abstraction over the transport specific connection used by a session actor.
/// </summary>
internal interface ISessionConnection : IAsyncDisposable
{
	/// <summary>
	/// Sends a payload to the remote client.
	/// </summary>
	ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

	/// <summary>
	/// Closes the connection.
	/// </summary>
	ValueTask CloseAsync(SessionCloseReason reason, string? description, CancellationToken cancellationToken);

	/// <summary>
	/// Gets the remote endpoint if available.
	/// </summary>
	System.Net.EndPoint? RemoteEndPoint { get; }

	/// <summary>
	/// Updates the last activity timestamp to support idle detection.
	/// </summary>
	void MarkActivity();

	/// <summary>
	/// Gets the timestamp of the last inbound/outbound activity.
	/// </summary>
	DateTimeOffset LastActivity { get; }
}

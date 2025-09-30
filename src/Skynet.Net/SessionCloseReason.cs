namespace Skynet.Net;

/// <summary>
/// Indicates why a session connection was terminated.
/// </summary>
public enum SessionCloseReason
{
	/// <summary>
	/// The remote client closed the connection gracefully.
	/// </summary>
	ClientDisconnected,

	/// <summary>
	/// The server shut down the session intentionally (e.g. application command).
	/// </summary>
	ServerShutdown,

	/// <summary>
	/// The session timed out because the client missed heartbeat/keep-alive messages.
	/// </summary>
	HeartbeatTimeout,

	/// <summary>
	/// The server detected a protocol or framing violation and terminated the session.
	/// </summary>
	ProtocolViolation,

	/// <summary>
	/// The session closed due to an unexpected transport failure.
	/// </summary>
	TransportError
}

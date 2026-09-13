namespace Skynet.Net;

/// <summary>
/// Lifecycle state of a <see cref="GateClient"/> connection loop.
/// </summary>
public enum GateClientState
{
	/// <summary>Not connected; the client is idle or between reconnect attempts.</summary>
	Disconnected,

	/// <summary>A connect attempt is in flight.</summary>
	Connecting,

	/// <summary>A connection is installed and healthy.</summary>
	Connected,

	/// <summary>Stopped by <see cref="GateClient.StopAsync"/>; no further reconnects until restarted.</summary>
	Stopped,
}

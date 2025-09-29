namespace Skynet.Net;

/// <summary>
/// Represents an inbound payload originating from the remote client.
/// </summary>
public sealed record SessionInboundMessage(byte[] Payload);

/// <summary>
/// Represents a server initiated payload being sent to the remote client.
/// </summary>
public sealed record SessionOutboundMessage(ReadOnlyMemory<byte> Payload, bool EndOfMessage = true);

/// <summary>
/// Requests the session actor to terminate the connection.
/// </summary>
public sealed record SessionCloseMessage(SessionCloseReason Reason, string? Description = null);

/// <summary>
/// Notification produced by the transport when the remote side disconnects.
/// </summary>
internal sealed record SessionClientClosedMessage(SessionCloseReason Reason, string? Description);

/// <summary>
/// Notification emitted when a heartbeat/idle timeout elapsed.
/// </summary>
internal sealed record SessionHeartbeatTimeoutMessage(TimeSpan Timeout);

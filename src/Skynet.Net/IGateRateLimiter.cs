namespace Skynet.Net;

/// <summary>
/// Decides how the gate reacts to a connection or inbound frame submitted to a rate limiter.
/// </summary>
public enum GateRateLimitDecision
{
	/// <summary>Accept the connection or dispatch the frame normally.</summary>
	Allow = 0,

	/// <summary>Discard the inbound frame without notifying the session actor; the connection stays open.</summary>
	Drop = 1,

	/// <summary>Reject the connection or terminate the session.</summary>
	Close = 2,
}

/// <summary>
/// Pluggable rate limiting hook evaluated by <see cref="GateServer"/>. Implementations must be safe for
/// concurrent use: every session pipeline calls these members from its own receive loop. Returning
/// <see cref="GateRateLimitDecision.Close"/> from <see cref="EvaluateConnection"/> rejects the connection
/// before any handshake or authentication work; returning it from <see cref="EvaluateInboundFrame"/>
/// closes the established session with <see cref="SessionCloseReason.ProtocolViolation"/>.
/// </summary>
public interface IGateRateLimiter
{
	/// <summary>
	/// Evaluates a newly accepted connection. Called once per connection before the session actor exists.
	/// Only <see cref="GateRateLimitDecision.Allow"/> and <see cref="GateRateLimitDecision.Close"/> are
	/// meaningful here; <see cref="GateRateLimitDecision.Drop"/> is treated as <see cref="GateRateLimitDecision.Allow"/>.
	/// </summary>
	/// <param name="metadata">Metadata captured at accept time.</param>
	GateRateLimitDecision EvaluateConnection(SessionMetadata metadata);

	/// <summary>
	/// Evaluates one inbound frame. Called for handshake and business frames alike so protectors can
	/// bound the cost of the RSA handshake; limiter burst sizes must therefore cover the handshake
	/// frame count (at most 3 frames in the built-in protocol).
	/// </summary>
	/// <param name="metadata">Metadata captured at accept time.</param>
	/// <param name="payloadSize">Size of the received payload in bytes.</param>
	GateRateLimitDecision EvaluateInboundFrame(SessionMetadata metadata, int payloadSize);

	/// <summary>
	/// Notifies the limiter that a session ended (including connections rejected at admission) so
	/// per-session state can be reclaimed.
	/// </summary>
	/// <param name="sessionId">Identifier of the session that ended.</param>
	void OnSessionClosed(string sessionId);
}

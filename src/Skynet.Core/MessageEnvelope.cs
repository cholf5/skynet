namespace Skynet.Core;

/// <summary>
/// Represents the metadata and payload associated with an actor message.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsResponse"/> marks envelopes produced by <see cref="WithResponse"/>. Transports use
/// it to route incoming envelopes: only response envelopes may complete a pending remote call.
/// Without the marker a peer's <em>request</em> whose <see cref="MessageId"/> collides with a
/// locally outstanding call id (each node allocates ids from its own counter) would be mistaken
/// for a response, corrupting the result and silently swallowing the request.
/// </para>
/// </remarks>
public sealed record MessageEnvelope(
	long MessageId,
	ActorHandle From,
	ActorHandle To,
	CallType CallType,
	object Payload,
	string? TraceId,
	DateTimeOffset Timestamp,
	TimeSpan? TimeToLive,
	int Version,
	bool IsResponse = false)
{
	/// <summary>
	/// Creates a response envelope derived from the current envelope.
	/// </summary>
	/// <param name="payload">The response payload.</param>
	/// <returns>A new envelope representing the response.</returns>
	public MessageEnvelope WithResponse(object payload)
	{
		return new MessageEnvelope(
			MessageId,
			To,
			From,
			CallType.Call,
			payload,
			TraceId,
			DateTimeOffset.UtcNow,
			TimeToLive,
			Version,
			IsResponse: true);
	}
}

namespace Skynet.Core;

/// <summary>
/// Represents the metadata and payload associated with an actor message.
/// </summary>
public sealed record MessageEnvelope(
	long MessageId,
	ActorHandle From,
	ActorHandle To,
	CallType CallType,
	object Payload,
	string? TraceId,
	DateTimeOffset Timestamp,
	TimeSpan? TimeToLive,
	int Version)
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
			Version);
	}
}

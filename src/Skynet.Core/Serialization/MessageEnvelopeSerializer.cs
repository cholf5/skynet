using MessagePack;

namespace Skynet.Core.Serialization;

[MessagePackObject]
public sealed class SerializedMessageEnvelope
{
	[Key(0)]
	public long MessageId { get; init; }

	[Key(1)]
	public long From { get; init; }

	[Key(2)]
	public long To { get; init; }

	[Key(3)]
	public CallType CallType { get; init; }

	[Key(4)]
	public required string PayloadType { get; init; }

	[Key(5)]
	public required byte[] Payload { get; init; }

	[Key(6)]
	public string? TraceId { get; init; }

	[Key(7)]
	public long Timestamp { get; init; }

	[Key(8)]
	public long? TimeToLiveTicks { get; init; }

	[Key(9)]
	public int Version { get; init; }
}

public static class MessageEnvelopeSerializer
{
	public static byte[] Serialize(MessageEnvelope envelope, MessagePackSerializerOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(envelope);
		options ??= MessagePackSerializerOptions.Standard;

		var payloadType = envelope.Payload.GetType();
		var payloadBytes = MessagePackSerializer.Serialize(payloadType, envelope.Payload, options);
		var dto = new SerializedMessageEnvelope
		{
			MessageId = envelope.MessageId,
			From = envelope.From.Value,
			To = envelope.To.Value,
			CallType = envelope.CallType,
			PayloadType = payloadType.AssemblyQualifiedName ?? payloadType.FullName ?? payloadType.Name,
			Payload = payloadBytes,
			TraceId = envelope.TraceId,
			Timestamp = envelope.Timestamp.UtcTicks,
			TimeToLiveTicks = envelope.TimeToLive?.Ticks,
			Version = envelope.Version
		};

		return MessagePackSerializer.Serialize(dto, options);
	}

	public static MessageEnvelope Deserialize(ReadOnlyMemory<byte> buffer, MessagePackSerializerOptions? options = null)
	{
		options ??= MessagePackSerializerOptions.Standard;
		var dto = MessagePackSerializer.Deserialize<SerializedMessageEnvelope>(buffer, options);
		var payloadType = Type.GetType(dto.PayloadType, throwOnError: true) ?? throw new InvalidOperationException($"Unable to resolve payload type '{dto.PayloadType}'.");
		var payload = MessagePackSerializer.Deserialize(payloadType, dto.Payload, options);
		var timestamp = new DateTimeOffset(dto.Timestamp, TimeSpan.Zero);
		TimeSpan? ttl = dto.TimeToLiveTicks.HasValue ? TimeSpan.FromTicks(dto.TimeToLiveTicks.Value) : null;

		return new MessageEnvelope(
			dto.MessageId,
			new ActorHandle(dto.From),
			new ActorHandle(dto.To),
			dto.CallType,
			payload!,
			dto.TraceId,
			timestamp,
			ttl,
			dto.Version);
	}

}

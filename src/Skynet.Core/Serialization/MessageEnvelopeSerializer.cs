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

	/// <summary>
	/// Stable contract id of the payload type; <see cref="PayloadContractRegistry.NullPayloadContractId"/>
	/// means the payload is <see langword="null"/>. Ids are resolved through
	/// <see cref="PayloadContractRegistry"/> instead of runtime string-to-type reflection.
	/// </summary>
	[Key(4)]
	public required int PayloadContractId { get; init; }

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

	/// <summary>
	/// Direction marker: <see langword="true"/> when this envelope is a response produced by
	/// <see cref="MessageEnvelope.WithResponse"/>. Receivers must only match pending remote calls
	/// against response envelopes; request frames never complete a pending call.
	/// </summary>
	[Key(10)]
	public bool IsResponse { get; init; }
}

public static class MessageEnvelopeSerializer
{
	/// <summary>
	/// Wire protocol version that replaced the string <c>PayloadType</c> (AssemblyQualifiedName)
	/// field with the int <c>PayloadContractId</c> field. Version 3 added the <c>IsResponse</c>
	/// direction marker that prevents cross-node MessageId collisions between concurrent requests
	/// and pending-call responses. Envelopes below this version are rejected.
	/// </summary>
	public const int WireVersion = 3;

	private const string LegacyVersionMessage =
		"Message envelope wire protocol versions below 3 (legacy string 'PayloadType', or version 2 " +
		"without the 'IsResponse' direction marker) are no longer supported; this node requires wire " +
		"protocol version 3. Upgrade every Skynet node in the cluster to a release that uses " +
		"direction-marker based envelopes.";

	public static byte[] Serialize(MessageEnvelope envelope, MessagePackSerializerOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(envelope);
		options ??= MessagePackSerializerOptions.Standard;

		var payloadType = envelope.Payload?.GetType();
		int contractId;
		byte[] payloadBytes;
		if (payloadType is null)
		{
			contractId = PayloadContractRegistry.NullPayloadContractId;
			payloadBytes = Array.Empty<byte>();
		}
		else
		{
			// The sender knows the concrete type: self-register it on first use so the wire only
			// ever carries the stable contract id.
			contractId = PayloadContractRegistry.GetOrRegister(payloadType);
			payloadBytes = MessagePackSerializer.Serialize(payloadType, envelope.Payload, options);
		}

		var dto = new SerializedMessageEnvelope
		{
			MessageId = envelope.MessageId,
			From = envelope.From.Value,
			To = envelope.To.Value,
			CallType = envelope.CallType,
			PayloadContractId = contractId,
			Payload = payloadBytes,
			TraceId = envelope.TraceId,
			Timestamp = envelope.Timestamp.UtcTicks,
			TimeToLiveTicks = envelope.TimeToLive?.Ticks,
			Version = envelope.Version,
			IsResponse = envelope.IsResponse
		};

		return MessagePackSerializer.Serialize(dto, options);
	}

	public static MessageEnvelope Deserialize(ReadOnlyMemory<byte> buffer, MessagePackSerializerOptions? options = null)
	{
		options ??= MessagePackSerializerOptions.Standard;
		SerializedMessageEnvelope dto;
		try
		{
			dto = MessagePackSerializer.Deserialize<SerializedMessageEnvelope>(buffer, options);
		}
		catch (MessagePackSerializationException exception)
		{
			if (IsLegacyWireFormat(buffer, options))
			{
				throw new NotSupportedException(LegacyVersionMessage, exception);
			}

			throw;
		}

		if (dto.Version < WireVersion)
		{
			throw new NotSupportedException(LegacyVersionMessage);
		}

		object? payload;
		if (dto.PayloadContractId == PayloadContractRegistry.NullPayloadContractId)
		{
			payload = null;
		}
		else if (!PayloadContractRegistry.TryResolve(dto.PayloadContractId, out var payloadType))
		{
			// Never fall back to Type.GetType: unknown contract ids must fail loudly so version
			// drift between nodes surfaces as an explicit error instead of arbitrary type loading.
			throw new UnknownPayloadContractException(dto.PayloadContractId);
		}
		else
		{
			payload = MessagePackSerializer.Deserialize(payloadType, dto.Payload, options);
		}

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
			dto.Version,
			dto.IsResponse);
	}

	/// <summary>
	/// Detects version 1 envelopes (string payload type name at key 4). Their bytes cannot be read
	/// with the current layout, so a legacy probe type is used to distinguish an old peer from a
	/// genuinely malformed frame.
	/// </summary>
	private static bool IsLegacyWireFormat(ReadOnlyMemory<byte> buffer, MessagePackSerializerOptions options)
	{
		try
		{
			var legacy = MessagePackSerializer.Deserialize<LegacySerializedMessageEnvelope>(buffer, options);
			return legacy.Version < WireVersion;
		}
		catch (MessagePackSerializationException)
		{
			return false;
		}
	}

	/// <summary>
	/// Mirror of the version 1 wire layout (string payload type name) used only to produce a clear
	/// error message when a legacy node is encountered.
	/// </summary>
	[MessagePackObject(AllowPrivate = true)]
	internal sealed class LegacySerializedMessageEnvelope
	{
		[Key(0)]
		public long MessageId { get; set; }

		[Key(1)]
		public long From { get; set; }

		[Key(2)]
		public long To { get; set; }

		[Key(3)]
		public CallType CallType { get; set; }

		[Key(4)]
		public string? PayloadType { get; set; }

		[Key(5)]
		public byte[]? Payload { get; set; }

		[Key(6)]
		public string? TraceId { get; set; }

		[Key(7)]
		public long Timestamp { get; set; }

		[Key(8)]
		public long? TimeToLiveTicks { get; set; }

		[Key(9)]
		public int Version { get; set; }
	}
}

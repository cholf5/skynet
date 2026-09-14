using FluentAssertions;
using MessagePack;
using Skynet.Core;
using Skynet.Core.Serialization;
using Xunit;

namespace Skynet.Core.Tests;

public sealed class MessageEnvelopeSerializerTests
{
	[Fact]
	public void Deserialize_ShouldThrowForUnknownPayloadContractId()
	{
		var buffer = MessagePackSerializer.Serialize(CreateDto(contractId: 555999111));

		var act = () => MessageEnvelopeSerializer.Deserialize(buffer);

		act.Should().Throw<UnknownPayloadContractException>()
			.Which.ContractId.Should().Be(555999111);
	}

	[Fact]
	public void TryDeserialize_ShouldReturnEnvelopeWithUnknownContractId()
	{
		var dto = CreateDto(contractId: 555999111);
		var buffer = MessagePackSerializer.Serialize(dto);

		var parsed = MessageEnvelopeSerializer.TryDeserialize(buffer, out var envelope, out var unknownContractId);

		parsed.Should().BeTrue("the envelope itself parsed; only the payload type is unknown");
		unknownContractId.Should().Be(555999111);
		envelope.Should().NotBeNull();
		envelope!.MessageId.Should().Be(dto.MessageId);
		envelope.IsResponse.Should().BeFalse("the transport uses the envelope to build a fault response");
		envelope.Payload.Should().BeNull("an unknown payload can never be materialized");
	}

	[Fact]
	public void TryDeserialize_ShouldReturnZeroUnknownIdForResolvedPayload()
	{
		var dto = CreateDto(PayloadContractRegistry.ComputeContractId("System.String"),
			MessagePackSerializer.Serialize("hello"));
		var buffer = MessagePackSerializer.Serialize(dto);

		var parsed = MessageEnvelopeSerializer.TryDeserialize(buffer, out var envelope, out var unknownContractId);

		parsed.Should().BeTrue();
		unknownContractId.Should().Be(PayloadContractRegistry.NullPayloadContractId);
		envelope!.Payload.Should().Be("hello");
	}

	[Fact]
	public void TryDeserialize_ShouldReturnFalseForMalformedFrame()
	{
		// 0xc1 is the "never used" MessagePack code: no valid envelope can start with it.
		var parsed = MessageEnvelopeSerializer.TryDeserialize(new byte[] { 0xc1 }, out var envelope,
			out var unknownContractId);

		parsed.Should().BeFalse("the transport must drop unparseable frames instead of tearing the connection down");
		envelope.Should().BeNull();
		unknownContractId.Should().Be(PayloadContractRegistry.NullPayloadContractId);
	}

	[Fact]
	public void TryDeserialize_ShouldReturnFalseWhenPayloadBytesDoNotFitResolvedType()
	{
		// The envelope resolves to string, but the payload bytes are not a string: the frame is
		// corrupt and must be dropped (false), not throw into the transport's read loop.
		var dto = CreateDto(PayloadContractRegistry.ComputeContractId("System.String"), new byte[] { 0xff });
		var buffer = MessagePackSerializer.Serialize(dto);

		var parsed = MessageEnvelopeSerializer.TryDeserialize(buffer, out var envelope, out _);

		parsed.Should().BeFalse();
		envelope.Should().BeNull();
	}

	[Fact]
	public void TryDeserialize_ShouldStillThrowForLegacyWireVersion()
	{
		// Legacy wire versions must keep failing loudly (mixed-version clusters), so TryDeserialize
		// deliberately does not swallow NotSupportedException.
		var dto = CreateDto(PayloadContractRegistry.ComputeContractId("System.String"),
			new byte[] { 1 }, version: MessageEnvelopeSerializer.WireVersion - 1);
		var buffer = MessagePackSerializer.Serialize(dto);

		var act = () => MessageEnvelopeSerializer.TryDeserialize(buffer, out _, out _);

		act.Should().Throw<NotSupportedException>();
	}

	private static SerializedMessageEnvelope CreateDto(int contractId, byte[]? payload = null, int version = 3)
	{
		return new SerializedMessageEnvelope
		{
			MessageId = 1234,
			From = 1,
			To = 2,
			CallType = CallType.Call,
			PayloadContractId = contractId,
			Payload = payload ?? new byte[] { 1 },
			TraceId = null,
			Timestamp = DateTimeOffset.UtcNow.UtcTicks,
			TimeToLiveTicks = null,
			Version = version
		};
	}
}

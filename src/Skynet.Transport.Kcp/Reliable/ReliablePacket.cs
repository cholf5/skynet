using System.Buffers.Binary;

namespace Skynet.Transport.Kcp.Reliable;

/// <summary>
/// Represents a packet exchanged between two <see cref="ReliableQueue"/> peers.
/// </summary>
/// <param name="Type">The packet type.</param>
/// <param name="Sn">The sequence number of the data segment (or the acknowledged segment for acks).</param>
/// <param name="Una">The receiver's next expected sequence number; everything below it is acknowledged.</param>
/// <param name="Context">Optional routing context carried alongside the message.</param>
/// <param name="Message">The message payload.</param>
public sealed record ReliablePacket(
	ReliablePacketType Type,
	ushort Sn,
	ushort Una,
	byte[] Context,
	byte[] Message)
{
	public static ReliablePacket Data(ushort sn, ushort una, ReadOnlyMemory<byte> context, ReadOnlyMemory<byte> message)
	{
		return new ReliablePacket(ReliablePacketType.Data, sn, una, context.ToArray(), message.ToArray());
	}

	public static ReliablePacket Ack(ushort sn, ushort una)
	{
		return new ReliablePacket(ReliablePacketType.Ack, sn, una, [], []);
	}
}

/// <summary>
/// Encodes and decodes <see cref="ReliablePacket"/> instances on the wire. The layout is a fixed
/// 13-byte header followed by the context and message payloads (all integers little-endian):
/// <c>type:1 | sn:2 | una:2 | contextLength:4 | messageLength:4</c>.
/// </summary>
public static class ReliablePacketCodec
{
	/// <summary>Gets the size in bytes of the fixed packet header.</summary>
	public const int HeaderLength = 13;

	public static byte[] Serialize(ReliablePacket packet)
	{
		ArgumentNullException.ThrowIfNull(packet);
		ArgumentNullException.ThrowIfNull(packet.Context);
		ArgumentNullException.ThrowIfNull(packet.Message);

		var buffer = new byte[HeaderLength + packet.Context.Length + packet.Message.Length];
		buffer[0] = (byte)packet.Type;
		BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(1, 2), packet.Sn);
		BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(3, 2), packet.Una);
		BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(5, 4), packet.Context.Length);
		BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(9, 4), packet.Message.Length);
		packet.Context.CopyTo(buffer, HeaderLength);
		packet.Message.CopyTo(buffer, HeaderLength + packet.Context.Length);
		return buffer;
	}

	public static ReliablePacket Deserialize(ReadOnlyMemory<byte> bytes)
	{
		if (bytes.Length < HeaderLength)
		{
			throw new InvalidDataException(
				$"Reliable packet is truncated: expected at least {HeaderLength} bytes but contained {bytes.Length}.");
		}

		var span = bytes.Span;
		var type = (ReliablePacketType)span[0];
		if (type is not (ReliablePacketType.Data or ReliablePacketType.Ack))
		{
			throw new InvalidDataException($"Reliable packet contains an unknown packet type {span[0]}.");
		}

		var sn = BinaryPrimitives.ReadUInt16LittleEndian(span[1..3]);
		var una = BinaryPrimitives.ReadUInt16LittleEndian(span[3..5]);
		var contextLength = BinaryPrimitives.ReadInt32LittleEndian(span[5..9]);
		var messageLength = BinaryPrimitives.ReadInt32LittleEndian(span[9..13]);
		if (contextLength < 0 || messageLength < 0 || HeaderLength + contextLength + messageLength != bytes.Length)
		{
			throw new InvalidDataException(
				$"Reliable packet header announces {contextLength} context bytes and {messageLength} message bytes " +
				$"which does not match the {bytes.Length}-byte packet.");
		}

		var context = bytes.Slice(HeaderLength, contextLength).ToArray();
		var message = bytes.Slice(HeaderLength + contextLength, messageLength).ToArray();
		return new ReliablePacket(type, sn, una, context, message);
	}
}

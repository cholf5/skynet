using FluentAssertions;
using Skynet.Cluster.Transport.Reliable;
using Xunit;

namespace Skynet.Core.Tests.Reliable;

public sealed class ReliableQueueTests
{
	[Fact]
	public void Ack_ShouldRemoveSentSegment()
	{
		var sent = new List<ReliablePacket>();
		var queue = new ReliableQueue(sent.Add, (_, _) => { });
		var now = DateTimeOffset.UnixEpoch;

		queue.Send(new byte[] { 1 }, now: now);
		queue.Input(ReliablePacket.Ack(0, 1), now);

		sent.Should().HaveCount(1);
		queue.SendQueueCount.Should().Be(0);
	}

	[Fact]
	public void OutOfOrderData_ShouldBeBufferedAndDeliveredInOrder()
	{
		var sent = new List<ReliablePacket>();
		var delivered = new List<byte[]>();
		var queue = new ReliableQueue(sent.Add, (_, message) => delivered.Add(message));

		queue.Input(ReliablePacket.Data(1, 0, Array.Empty<byte>(), new byte[] { 2 }));
		queue.Input(ReliablePacket.Data(0, 0, Array.Empty<byte>(), new byte[] { 1 }));

		sent.Should().HaveCount(2);
		delivered.Should().HaveCount(2);
		delivered[0].Should().Equal(new byte[] { 1 });
		delivered[1].Should().Equal(new byte[] { 2 });
		queue.RecvBufferCount.Should().Be(0);
		queue.RecvNext.Should().Be(2);
	}

	[Fact]
	public void WindowFull_ShouldBufferMessagesUntilAckAdvancesWindow()
	{
		var sent = new List<ReliablePacket>();
		var queue = new ReliableQueue(sent.Add, (_, _) => { }, windowSize: 1);
		var now = DateTimeOffset.UnixEpoch;

		queue.Send(new byte[] { 1 }, now: now);
		queue.Send(new byte[] { 2 }, now: now);

		sent.Should().HaveCount(1);
		queue.SendQueueCount.Should().Be(1);
		queue.SendBufferCount.Should().Be(1);

		queue.Input(ReliablePacket.Ack(0, 1), now);

		sent.Should().HaveCount(2);
		queue.SendQueueCount.Should().Be(1);
		queue.SendBufferCount.Should().Be(0);
		sent[1].Sn.Should().Be(1);
	}

	[Fact]
	public void Update_ShouldRetransmitTimedOutSegment()
	{
		var sent = new List<ReliablePacket>();
		var queue = new ReliableQueue(sent.Add, (_, _) => { }, initialRto: TimeSpan.FromMilliseconds(100));
		var now = DateTimeOffset.UnixEpoch;

		queue.Send(new byte[] { 1 }, now: now);
		queue.Update(now.AddMilliseconds(100));

		sent.Should().HaveCount(2);
		sent[1].Sn.Should().Be(sent[0].Sn);
	}

	[Fact]
	public void DataTooFarAhead_ShouldNotBeBuffered()
	{
		var delivered = new List<byte[]>();
		var queue = new ReliableQueue(_ => { }, (_, message) => delivered.Add(message), windowSize: 32);

		queue.Input(ReliablePacket.Data(1, 0, Array.Empty<byte>(), new byte[] { 1 }));
		// 64 - 0 (recvNext) >= window 32: beyond the receive window, must be dropped.
		queue.Input(ReliablePacket.Data(64, 0, Array.Empty<byte>(), new byte[] { 2 }));

		queue.RecvBufferCount.Should().Be(1);
		delivered.Should().BeEmpty();
	}

	[Fact]
	public void SequenceNumbers_ShouldWrapAroundAndKeepOrdering()
	{
		var delivered = new List<byte[]>();
		var queue = new ReliableQueue(_ => { }, (_, message) => delivered.Add(message), windowSize: 32);

		// Drive the receive counter to 65535: (short)(left - right) < 0 semantics must then order
		// 65535 -> 0 -> 1 across the wraparound instead of treating 0 as "far in the past".
		for (var sn = 0; sn < ushort.MaxValue; sn++)
		{
			queue.Input(ReliablePacket.Data((ushort)sn, 0, Array.Empty<byte>(), new byte[] { (byte)(sn & 0xFF) }));
		}

		queue.Input(ReliablePacket.Data(ushort.MaxValue, 0, Array.Empty<byte>(), new byte[] { 0xFF }));
		queue.Input(ReliablePacket.Data(0, 0, Array.Empty<byte>(), new byte[] { 0xAA }));
		queue.Input(ReliablePacket.Data(1, 0, Array.Empty<byte>(), new byte[] { 0xBB }));

		delivered.Should().HaveCount(ushort.MaxValue + 3);
		delivered[^3].Should().Equal(new byte[] { 0xFF });
		delivered[^2].Should().Equal(new byte[] { 0xAA });
		delivered[^1].Should().Equal(new byte[] { 0xBB });
		queue.RecvNext.Should().Be(2);
	}

	[Fact]
	public void DuplicateAcknowledgements_ShouldTriggerFastResendBeforeRto()
	{
		var sent = new List<ReliablePacket>();
		var queue = new ReliableQueue(sent.Add, (_, _) => { }, initialRto: TimeSpan.FromSeconds(10));
		var now = DateTimeOffset.UnixEpoch;

		for (var i = 0; i < 4; i++)
		{
			queue.Send(new byte[] { (byte)i }, now: now);
		}

		// Three duplicate acks for sn 3 leave segments 0..2 unacked with FastAck == 3 each.
		queue.Input(ReliablePacket.Ack(3, 0), now);
		queue.Input(ReliablePacket.Ack(3, 0), now);
		queue.Input(ReliablePacket.Ack(3, 0), now);
		// Far below the 10 second RTO: the resend must come from fast resend, not the timer.
		queue.Update(now.AddMilliseconds(1));

		sent.Should().HaveCount(7);
		sent.Skip(4).Select(packet => packet.Sn).Should().Equal(new ushort[] { 0, 1, 2 });
	}

	[Fact]
	public void Rto_ShouldAdaptToRttSamples()
	{
		var queue = new ReliableQueue(_ => { }, (_, _) => { },
			initialRto: TimeSpan.FromSeconds(2), minRto: TimeSpan.FromMilliseconds(50));
		var now = DateTimeOffset.UnixEpoch;

		// First sample of 20 ms: SRTT = 20, RTTVAR = 10, RTO = 20 + max(10, 4 * 10) = 60 ms.
		queue.Send(new byte[] { 1 }, now: now);
		now += TimeSpan.FromMilliseconds(20);
		queue.Input(ReliablePacket.Ack(0, 1), now);
		queue.CurrentRto.Should().Be(TimeSpan.FromMilliseconds(60));

		// Second sample of 100 ms: RTTVAR = 27.5, SRTT = 30, RTO = 30 + 110 = 140 ms.
		queue.Send(new byte[] { 2 }, now: now);
		now += TimeSpan.FromMilliseconds(100);
		queue.Input(ReliablePacket.Ack(1, 2), now);
		queue.CurrentRto.Should().Be(TimeSpan.FromMilliseconds(140));
	}

	[Fact]
	public void Rto_ShouldRespectTheConfiguredMinimum()
	{
		var queue = new ReliableQueue(_ => { }, (_, _) => { },
			initialRto: TimeSpan.FromSeconds(2), minRto: TimeSpan.FromMilliseconds(50));
		var now = DateTimeOffset.UnixEpoch;

		// A 1 ms sample would produce an RTO of 1 + max(10, 2) = 11 ms without the floor.
		queue.Send(new byte[] { 1 }, now: now);
		now += TimeSpan.FromMilliseconds(1);
		queue.Input(ReliablePacket.Ack(0, 1), now);

		queue.CurrentRto.Should().Be(TimeSpan.FromMilliseconds(50));
	}

	[Fact]
	public void Rto_ShouldNotSampleRetransmittedSegments()
	{
		var queue = new ReliableQueue(_ => { }, (_, _) => { },
			initialRto: TimeSpan.FromSeconds(2), minRto: TimeSpan.FromMilliseconds(50));
		var now = DateTimeOffset.UnixEpoch;

		queue.Send(new byte[] { 1 }, now: now);
		// Force a timeout retransmission; the segment's Xmit becomes 2.
		queue.Update(now.AddSeconds(2));
		var rtoBeforeAck = queue.CurrentRto;

		// Karn's algorithm: an acknowledgement of a retransmitted segment yields no RTT sample.
		queue.Input(ReliablePacket.Ack(0, 1), now.AddSeconds(3));

		queue.CurrentRto.Should().Be(rtoBeforeAck);
	}

	[Fact]
	public void BrokenLink_ShouldEnterDeadTerminalStateWithoutCallbackStorm()
	{
		var brokenCount = 0;
		var queue = new ReliableQueue(_ => { }, (_, _) => { }, () => brokenCount++,
			initialRto: TimeSpan.FromMilliseconds(1), deadLink: 2, minRto: TimeSpan.FromMilliseconds(1));
		var now = DateTimeOffset.UnixEpoch;

		queue.Send(new byte[] { 1 }, now: now);
		queue.Update(now.AddMilliseconds(1)); // first timeout retransmission (Xmit = 2)
		queue.Update(now.AddMilliseconds(2)); // retransmission budget exhausted -> broken

		brokenCount.Should().Be(1);
		queue.IsBroken.Should().BeTrue();

		// The bad segment must be removed and the terminal state must not storm the callback.
		queue.SendQueueCount.Should().Be(0);
		for (var i = 0; i < 50; i++)
		{
			queue.Update(now.AddSeconds(1 + i));
		}

		brokenCount.Should().Be(1);

		// While broken the queue rejects new work and silently drops input.
		var send = () => queue.Send(new byte[] { 9 });
		send.Should().Throw<InvalidOperationException>();
		queue.Input(ReliablePacket.Data(0, 0, Array.Empty<byte>(), new byte[] { 1 }));
		queue.RecvNext.Should().Be(0);
	}

	[Fact]
	public void Reset_ShouldRestoreABrokenQueueForReuse()
	{
		var brokenCount = 0;
		var sent = new List<ReliablePacket>();
		var delivered = new List<byte[]>();
		var queue = new ReliableQueue(sent.Add, (_, message) => delivered.Add(message), () => brokenCount++,
			initialRto: TimeSpan.FromMilliseconds(1), deadLink: 2, minRto: TimeSpan.FromMilliseconds(1));
		var now = DateTimeOffset.UnixEpoch;

		queue.Send(new byte[] { 1 }, now: now);
		queue.Update(now.AddMilliseconds(1));
		queue.Update(now.AddMilliseconds(2));
		queue.IsBroken.Should().BeTrue();

		queue.Reset();

		queue.IsBroken.Should().BeFalse();
		queue.SendNext.Should().Be(0);
		queue.RecvNext.Should().Be(0);
		queue.CurrentRto.Should().Be(TimeSpan.FromMilliseconds(1));

		// The reset queue can run a full exchange again.
		queue.Send(new byte[] { 42 }, now: now);
		sent[^1].Sn.Should().Be(0);
		queue.Input(ReliablePacket.Data(0, 0, Array.Empty<byte>(), new byte[] { 7 }), now);
		delivered.Should().ContainSingle().Which.Should().Equal(new byte[] { 7 });
		brokenCount.Should().Be(1);
	}
}

/// <summary>
/// Codec tests for the reliable-layer packet encoding used to bridge a ReliableQueue onto a raw
/// datagram channel.
/// </summary>
public sealed class ReliablePacketCodecTests
{
	[Fact]
	public void DataPacket_ShouldRoundtrip()
	{
		var packet = ReliablePacket.Data(65530, 12, new byte[] { 1, 2, 3 }, new byte[] { 9, 8 });

		var decoded = ReliablePacketCodec.Deserialize(ReliablePacketCodec.Serialize(packet));

		decoded.Type.Should().Be(ReliablePacketType.Data);
		decoded.Sn.Should().Be(packet.Sn);
		decoded.Una.Should().Be(packet.Una);
		decoded.Context.Should().Equal(packet.Context);
		decoded.Message.Should().Equal(packet.Message);
	}

	[Fact]
	public void AckPacket_ShouldRoundtrip()
	{
		var packet = ReliablePacket.Ack(42, 7);

		var decoded = ReliablePacketCodec.Deserialize(ReliablePacketCodec.Serialize(packet));

		decoded.Type.Should().Be(ReliablePacketType.Ack);
		decoded.Sn.Should().Be(42);
		decoded.Una.Should().Be(7);
		decoded.Context.Should().BeEmpty();
		decoded.Message.Should().BeEmpty();
	}

	[Fact]
	public void Deserialize_ShouldRejectTruncatedPackets()
	{
		var bytes = ReliablePacketCodec.Serialize(ReliablePacket.Ack(1, 1));
		var act = () => ReliablePacketCodec.Deserialize(bytes.AsMemory(0, ReliablePacketCodec.HeaderLength - 1));
		act.Should().Throw<InvalidDataException>();
	}

	[Fact]
	public void Deserialize_ShouldRejectUnknownPacketType()
	{
		var bytes = ReliablePacketCodec.Serialize(ReliablePacket.Ack(1, 1));
		bytes[0] = 0xAB;
		var act = () => ReliablePacketCodec.Deserialize(bytes);
		act.Should().Throw<InvalidDataException>();
	}

	[Fact]
	public void Deserialize_ShouldRejectLengthMismatch()
	{
		var bytes = ReliablePacketCodec.Serialize(ReliablePacket.Ack(1, 1));
		// The header announces two zero-length payloads; trailing bytes violate that announcement.
		var padded = new byte[bytes.Length + 3];
		bytes.CopyTo(padded, 0);
		var act = () => ReliablePacketCodec.Deserialize(padded);
		act.Should().Throw<InvalidDataException>();
	}
}

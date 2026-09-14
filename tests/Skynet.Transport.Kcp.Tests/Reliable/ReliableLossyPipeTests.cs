using System.Buffers.Binary;
using FluentAssertions;
using Skynet.Transport.Kcp.Reliable;
using Xunit;

namespace Skynet.Transport.Kcp.Tests.Reliable;

/// <summary>
/// End-to-end tests that wire two ReliableQueue peers together through an injectable fake channel
/// which drops, delays, and reorders packets, driven by a virtual clock (no real waiting).
/// </summary>
public sealed class ReliableLossyPipeTests
{
	public static TheoryData<double, int> LossScenarios => new()
	{
		// (drop rate, message count)
		{ 0.05, 500 },
		{ 0.20, 250 }
	};

	[Theory]
	[MemberData(nameof(LossScenarios))]
	public void ReliableQueue_ShouldDeliverEverythingInOrderUnderLoss(double dropRate, int messageCount)
	{
		var result = RunExchange(dropRate, messageCount,
			TimeSpan.FromMilliseconds(200), deadLink: 20);

		result.Delivered.Should().HaveCount(messageCount, "diagnostics: {0}", result.Diagnostics);
		result.Delivered.Should().BeInAscendingOrder("the reliable layer must preserve ordering");
		result.Delivered[^1].Should().Be(messageCount - 1);
		result.Broken.Should().BeFalse("a {0:P0} drop rate must not exhaust the dead-link budget", dropRate);
	}

	[Fact]
	public void ReliableQueue_ShouldDeclareBrokenLinkWhenEveryPacketIsDropped()
	{
		var result = RunExchange(dropRate: 1.0, messageCount: 4,
			TimeSpan.FromMilliseconds(50), deadLink: 5);

		result.Broken.Should().BeTrue();
		result.Delivered.Should().BeEmpty();
	}

	private static ExchangeResult RunExchange(double dropRate, int messageCount, TimeSpan initialRto,
		ushort deadLink)
	{
		const int tickMilliseconds = 10;
		var start = DateTimeOffset.UnixEpoch;
		// Jitter well above the per-tick send spacing guarantees that the fake channel delivers
		// due packets out of order, exercising reordering as well as loss.
		var clientToServer = new LossyLink(seed: 20260913, dropRate,
			latency: TimeSpan.FromMilliseconds(5), jitter: TimeSpan.FromMilliseconds(15));
		var serverToClient = new LossyLink(seed: 451, dropRate,
			latency: TimeSpan.FromMilliseconds(5), jitter: TimeSpan.FromMilliseconds(15));
		var client = new ReliableEndpoint(clientToServer, initialRto, deadLink);
		var server = new ReliableEndpoint(serverToClient, initialRto, deadLink);

		for (var i = 0; i < messageCount; i++)
		{
			var payload = new byte[4];
			BinaryPrimitives.WriteInt32LittleEndian(payload, i);
			client.Queue.Send(payload, now: start);
		}

		var now = start;
		var deadline = start.AddMinutes(10);
		while (now < deadline)
		{
			now = now.AddMilliseconds(tickMilliseconds);
			client.AdvanceTo(now);
			server.AdvanceTo(now);

			// Retransmission timers fire, then everything that became due is handed to the peer.
			client.Queue.Update(now);
			server.Queue.Update(now);
			clientToServer.Deliver(now, server);
			serverToClient.Deliver(now, client);

			if (server.Received.Count >= messageCount)
			{
				break;
			}
		}

		return new ExchangeResult(
			server.Received.Select(bytes => BinaryPrimitives.ReadInt32LittleEndian(bytes)).ToList(),
			client.Queue.IsBroken || server.Queue.IsBroken,
			$"client: sq={client.Queue.SendQueueCount} sb={client.Queue.SendBufferCount} sn={client.Queue.SendNext} rn={client.Queue.RecvNext} rto={client.Queue.CurrentRto.TotalMilliseconds:F0}ms | " +
			$"server: sq={server.Queue.SendQueueCount} sb={server.Queue.SendBufferCount} sn={server.Queue.SendNext} rn={server.Queue.RecvNext} rb={server.Queue.RecvBufferCount} rto={server.Queue.CurrentRto.TotalMilliseconds:F0}ms | " +
			$"inflight c2s={clientToServer.PendingCount} s2c={serverToClient.PendingCount}");
	}

	/// <summary>One end of a simulated exchange: a queue plus the lossy link it sends on.</summary>
	private sealed class ReliableEndpoint
	{
		private readonly LossyLink _outbound;
		private DateTimeOffset _now;

		public ReliableEndpoint(LossyLink outbound, TimeSpan initialRto, ushort deadLink)
		{
			_outbound = outbound;
			Queue = new ReliableQueue(
				packet => _outbound.TrySend(packet, _now),
				(_, message) => Received.Add(message),
				windowSize: 32,
				initialRto: initialRto,
				fastResend: 3,
				deadLink: deadLink,
				minRto: TimeSpan.FromMilliseconds(50));
		}

		public ReliableQueue Queue { get; }

		public List<byte[]> Received { get; } = [];

		public void AdvanceTo(DateTimeOffset now)
		{
			_now = now;
		}
	}

	private sealed class ExchangeResult(List<int> delivered, bool broken, string diagnostics)
	{
		public List<int> Delivered { get; } = delivered;

		public bool Broken { get; } = broken;

		public string Diagnostics { get; } = diagnostics;
	}

	/// <summary>
	/// A fake one-way channel with injectable drop rate, fixed latency, and uniform jitter. Jitter
	/// makes the delivery order of packets that became due in the same tick nondeterministic, so
	/// out-of-order arrivals reach the peer's ReliableQueue naturally.
	/// </summary>
	private sealed class LossyLink(int seed, double dropRate, TimeSpan latency, TimeSpan jitter)
	{
		private readonly Random _random = new(seed);
		private readonly List<(DateTimeOffset DeliverAt, ReliablePacket Packet)> _inFlight = [];

		public bool TrySend(ReliablePacket packet, DateTimeOffset now)
		{
			if (_random.NextDouble() < dropRate)
			{
				return false;
			}

			var delay = latency + TimeSpan.FromMilliseconds(_random.NextDouble() * jitter.TotalMilliseconds);
			_inFlight.Add((now + delay, packet));
			return true;
		}

		public int PendingCount => _inFlight.Count;

		public void Deliver(DateTimeOffset now, ReliableEndpoint peer)
		{
			for (var i = _inFlight.Count - 1; i >= 0; i--)
			{
				if (_inFlight[i].DeliverAt > now)
				{
					continue;
				}

				var packet = _inFlight[i].Packet;
				_inFlight.RemoveAt(i);
				peer.Queue.Input(packet, now);
			}
		}
	}
}

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using Skynet.Cluster;
using Skynet.Core;
using Xunit;

namespace Skynet.Core.Tests;

/// <summary>
/// Chaos integration tests (PRD 13.3): two real nodes wired over loopback TCP with a
/// <see cref="FaultInjectingStream"/> decorator injected through
/// <see cref="TcpTransportOptions.StreamDecorator"/>. Scenarios: sustained loss, delay jitter,
/// half-open links, manual disconnects, and node restart — every assertion is bounded in time
/// and every fault decision comes from a fixed seed, so the suite never hangs and reruns
/// deterministically.
/// </summary>
public sealed class TcpTransportFaultInjectionTests
{
	[Fact]
	public async Task Chaos_SustainedEnvelopeLoss_CallsEitherSucceedOrFailWithinBound()
	{
		var (port1, port2) = (GetFreePort(), GetFreePort());
		var configuration = BuildConfiguration(
			CreateNodeConfiguration("node1", port1, 1000, ("echo", 1001)),
			CreateNodeConfiguration("node2", port2, 2000));

		// 20% of envelope frames written by the caller vanish; handshake and heartbeat frames are
		// excluded so the link itself stays healthy and only calls are degraded.
		var callerStreams = new ConcurrentBag<FaultInjectingStream>();
		var callerOptions = new TcpTransportOptions
		{
			HeartbeatInterval = TimeSpan.FromMilliseconds(250),
			StreamDecorator = CaptureStreams(callerStreams, new FaultInjectionOptions
			{
				Seed = 20260915,
				DropRate = 0.20,
				FrameSelector = frame => frame.FrameType == FaultInjectionFrameType.Envelope
			})
		};

		await using var callee = CreateNode(configuration, "node1", new TcpTransportOptions
		{
			HeartbeatInterval = TimeSpan.FromMilliseconds(250)
		});
		await callee.System.CreateActorAsync(() => new ChaosEchoActor(), "echo",
			new ActorCreationOptions { HandleOverride = new ActorHandle(1001) });

		await using var caller = CreateNode(configuration, "node2", callerOptions);
		var remote = caller.System.GetByName("echo");

		const int rounds = 25;
		var succeeded = 0;
		var failed = 0;
		for (var i = 0; i < rounds; i++)
		{
			try
			{
				var answer = await remote.CallAsync<string>(new ChaosEchoRequest("msg-" + i), TimeSpan.FromSeconds(1));
				answer.Should().Be("echo:msg-" + i, "a delivered response must never be corrupted");
				succeeded++;
			}
			catch (TaskCanceledException)
			{
				// The request frame was dropped: the call fails in bounded time (the caller timeout)
				// instead of hanging forever.
				failed++;
			}
		}

		var chaos = callerStreams.Should().ContainSingle().Subject;
		(succeeded + failed).Should().Be(rounds);
		succeeded.Should().BeGreaterThan(0, "most calls must survive a 20% drop rate");
		failed.Should().BeGreaterThan(0, "the seeded 20% drop rate must remove at least one request frame");
		chaos.SelectedFrameCount.Should().Be(rounds, "only the request envelope frames are subject to faults");
		chaos.DroppedFrameCount.Should().Be(failed, "every dropped request frame times out exactly one call");

		// The link survived the chaos: a follow-up call gets through once its request frame does.
		var recovered = false;
		for (var attempt = 0; attempt < 20 && !recovered; attempt++)
		{
			try
			{
				(await remote.CallAsync<string>(new ChaosEchoRequest("after"), TimeSpan.FromSeconds(1)))
					.Should().Be("echo:after");
				recovered = true;
			}
			catch (TaskCanceledException)
			{
			}
		}

		recovered.Should().BeTrue("the connection must stay usable after sustained loss");
	}

	[Fact]
	public async Task Chaos_WriteDelayJitter_PreservesReceiverProcessingOrder()
	{
		var (port1, port2) = (GetFreePort(), GetFreePort());
		var configuration = BuildConfiguration(
			CreateNodeConfiguration("node1", port1, 1000, ("order", 1001)),
			CreateNodeConfiguration("node2", port2, 2000));

		var callerStreams = new ConcurrentBag<FaultInjectingStream>();
		var callerOptions = new TcpTransportOptions
		{
			HeartbeatInterval = TimeSpan.FromMilliseconds(250),
			StreamDecorator = CaptureStreams(callerStreams, new FaultInjectionOptions
			{
				Seed = 42,
				MinWriteDelay = TimeSpan.Zero,
				MaxWriteDelay = TimeSpan.FromMilliseconds(40),
				FrameSelector = frame => frame.FrameType == FaultInjectionFrameType.Envelope
			})
		};

		await using var callee = CreateNode(configuration, "node1", new TcpTransportOptions
		{
			HeartbeatInterval = TimeSpan.FromMilliseconds(250)
		});
		await callee.System.CreateActorAsync(() => new ChaosOrderingActor(), "order",
			new ActorCreationOptions { HandleOverride = new ActorHandle(1001) });

		await using var caller = CreateNode(configuration, "node2", callerOptions);
		var remote = caller.System.GetByName("order");

		const int messageCount = 25;
		for (var i = 0; i < messageCount; i++)
		{
			await remote.SendAsync(new ChaosOrderRequest(i));
		}

		ChaosOrderSnapshot? snapshot = null;
		for (var attempt = 0; attempt < 100 && snapshot is null; attempt++)
		{
			var current = await remote.CallAsync<ChaosOrderSnapshot>(new ChaosGetOrderSnapshot(), TimeSpan.FromSeconds(2));
			if (current.Indexes.Length >= messageCount)
			{
				snapshot = current;
			}
			else
			{
				await Task.Delay(50);
			}
		}

		snapshot.Should().NotBeNull("all messages must be delivered under delay jitter");
		snapshot!.Indexes.Should().HaveCount(messageCount);
		snapshot.Indexes.Should().Equal(Enumerable.Range(0, messageCount),
			"the receiving actor must process frames in wire order despite per-frame delay");

		var chaos = callerStreams.Single();
		chaos.DelayedFrameCount.Should().BeGreaterThanOrEqualTo(messageCount,
			"every request frame must have been held back by the delay distribution");
		chaos.DroppedFrameCount.Should().Be(0, "delay jitter must not lose frames");
	}

	[Fact]
	public async Task Chaos_HalfOpenLink_DeadNodeDetectionFailsPendingCallAndRecoveryWorks()
	{
		var (port1, port2) = (GetFreePort(), GetFreePort());
		var configuration = BuildConfiguration(
			CreateNodeConfiguration("node1", port1, 1000, ("echo", 1001)),
			CreateNodeConfiguration("node2", port2, 2000));

		// node1's stream decorator can stall reads (half-open); its dead-link detection matches the
		// D-3 defaults: heartbeats every 50 ms, a silent peer is declared dead after 300 ms.
		var calleeStreams = new ConcurrentBag<FaultInjectingStream>();
		var calleeOptions = new TcpTransportOptions
		{
			HeartbeatInterval = TimeSpan.FromMilliseconds(50),
			DeadNodeGracePeriod = TimeSpan.FromMilliseconds(300),
			StreamDecorator = CaptureStreams(calleeStreams, new FaultInjectionOptions { Seed = 7 })
		};

		await using var callee = CreateNode(configuration, "node1", calleeOptions);
		await callee.System.CreateActorAsync(() => new ChaosEchoActor(), "echo",
			new ActorCreationOptions { HandleOverride = new ActorHandle(1001) });

		await using var caller = CreateNode(configuration, "node2", new TcpTransportOptions
		{
			HeartbeatInterval = TimeSpan.FromMilliseconds(50)
		});
		var remote = caller.System.GetByName("echo");
		(await remote.CallAsync<string>(new ChaosEchoRequest("warmup"), TimeSpan.FromSeconds(10)))
			.Should().Be("echo:warmup");

		// From now on node1 stops consuming incoming frames (heartbeats included) but keeps the
		// connection open — a half-open link. The request below reaches the socket but is never
		// processed, and node1 must detect the silent peer within its grace period.
		var stalled = calleeStreams.Should().ContainSingle().Subject;
		stalled.StallReads();

		var hungCall = remote.CallAsync<string>(new ChaosEchoRequest("half-open"), TimeSpan.FromSeconds(30));
		Func<Task> act = async () => await hungCall;
		(await act.Should().ThrowAsync<RemoteConnectionClosedException>().WaitAsync(TimeSpan.FromSeconds(10)))
			.Which.NodeId.Should().Be("node1", "the half-open side must tear the link down via dead-node detection");

		// The dead link is gone on the caller side; a follow-up call dials a fresh, healthy link.
		for (var attempt = 0; attempt < 200 && caller.Transport._connections.Count > 0; attempt++)
		{
			await Task.Delay(25);
		}

		caller.Transport._connections.Should().BeEmpty("the caller must have released the dead connection");
		(await remote.CallAsync<string>(new ChaosEchoRequest("after"), TimeSpan.FromSeconds(10)))
			.Should().Be("echo:after", "a call after recovery must succeed on a fresh connection");
	}

	[Fact]
	public async Task Chaos_ManualDisconnect_FailsPendingCallBoundedAndRecovers()
	{
		var (port1, port2) = (GetFreePort(), GetFreePort());
		var configuration = BuildConfiguration(
			CreateNodeConfiguration("node1", port1, 1000, ("echo", 1001), ("slow", 1002)),
			CreateNodeConfiguration("node2", port2, 2000));

		var callerStreams = new ConcurrentBag<FaultInjectingStream>();
		var callerOptions = new TcpTransportOptions
		{
			HeartbeatInterval = TimeSpan.FromMilliseconds(250),
			StreamDecorator = CaptureStreams(callerStreams, new FaultInjectionOptions())
		};

		await using var callee = CreateNode(configuration, "node1", new TcpTransportOptions
		{
			HeartbeatInterval = TimeSpan.FromMilliseconds(250)
		});
		await callee.System.CreateActorAsync(() => new ChaosEchoActor(), "echo",
			new ActorCreationOptions { HandleOverride = new ActorHandle(1001) });
		await callee.System.CreateActorAsync(() => new ChaosSlowActor(), "slow",
			new ActorCreationOptions { HandleOverride = new ActorHandle(1002) });

		await using var caller = CreateNode(configuration, "node2", callerOptions);
		var echo = caller.System.GetByName("echo");
		var slow = caller.System.GetByName("slow");
		(await echo.CallAsync<string>(new ChaosEchoRequest("warmup"), TimeSpan.FromSeconds(10)))
			.Should().Be("echo:warmup");

		// A call in flight is killed by a manual disconnect of the link, not by the 30 s timeout.
		var hungCall = slow.CallAsync<string>(new ChaosEchoRequest("ping"), TimeSpan.FromSeconds(30));
		await Task.Delay(TimeSpan.FromMilliseconds(500));
		callerStreams.Single().BreakConnection();

		Func<Task> act = async () => await hungCall;
		(await act.Should().ThrowAsync<RemoteConnectionClosedException>().WaitAsync(TimeSpan.FromSeconds(10)))
			.Which.NodeId.Should().Be("node1");

		for (var attempt = 0; attempt < 200 && caller.Transport._connections.Count > 0; attempt++)
		{
			await Task.Delay(25);
		}

		caller.Transport._connections.Should().BeEmpty("the broken connection must be released");
		(await echo.CallAsync<string>(new ChaosEchoRequest("after"), TimeSpan.FromSeconds(10)))
			.Should().Be("echo:after", "a call after the disconnect must succeed on a fresh connection");
	}

	[Fact]
	public async Task Chaos_NodeRestartUnderFaultInjection_CallContinuesAfterReconnect()
	{
		var (port1, port2) = (GetFreePort(), GetFreePort());
		var configuration = BuildConfiguration(
			CreateNodeConfiguration("node1", port1, 1000, ("echo", 1001)),
			CreateNodeConfiguration("node2", port2, 2000));

		// The caller keeps writing through a delay-injecting stream across the whole lifecycle,
		// including the reconnect: every envelope is delayed by up to 25 ms, yet the restart
		// recovery must complete and deliver intact content.
		var callerStreams = new ConcurrentBag<FaultInjectingStream>();
		var callerOptions = new TcpTransportOptions
		{
			HeartbeatInterval = TimeSpan.FromMilliseconds(250),
			StreamDecorator = CaptureStreams(callerStreams, new FaultInjectionOptions
			{
				Seed = 20260915,
				MinWriteDelay = TimeSpan.Zero,
				MaxWriteDelay = TimeSpan.FromMilliseconds(25),
				FrameSelector = frame => frame.FrameType == FaultInjectionFrameType.Envelope
			})
		};

		await using var caller = CreateNode(configuration, "node2", callerOptions);
		var remote = caller.System.GetByName("echo");
		await using (var callee = CreateNode(configuration, "node1", new TcpTransportOptions
		{
			HeartbeatInterval = TimeSpan.FromMilliseconds(250)
		}))
		{
			await callee.System.CreateActorAsync(() => new ChaosEchoActor(), "echo",
				new ActorCreationOptions { HandleOverride = new ActorHandle(1001) });
			(await remote.CallAsync<string>(new ChaosEchoRequest("warmup"), TimeSpan.FromSeconds(10)))
				.Should().Be("echo:warmup");
		}

		for (var attempt = 0; attempt < 200 && caller.Transport._connections.Count > 0; attempt++)
		{
			await Task.Delay(25);
		}

		caller.Transport._connections.Should().BeEmpty("the dead connection's cleanup must have completed");

		await using var restarted = CreateNode(configuration, "node1", new TcpTransportOptions
		{
			HeartbeatInterval = TimeSpan.FromMilliseconds(250)
		});
		await restarted.System.CreateActorAsync(() => new ChaosEchoActor(), "echo",
			new ActorCreationOptions { HandleOverride = new ActorHandle(1001) });

		(await remote.CallAsync<string>(new ChaosEchoRequest("ping"), TimeSpan.FromSeconds(10)))
			.Should().Be("echo:ping", "the call must continue after the restart, under fault injection");
		callerStreams.Should().HaveCount(2, "the reconnect dials a fresh, separately injected connection");
	}

	private static Func<Stream, Stream> CaptureStreams(ConcurrentBag<FaultInjectingStream> sink,
		FaultInjectionOptions options)
	{
		return stream =>
		{
			var faulting = new FaultInjectingStream(stream, options);
			sink.Add(faulting);
			return faulting;
		};
	}

	private static ChaosNode CreateNode(
		StaticClusterConfiguration configuration, string nodeId, TcpTransportOptions options)
	{
		TcpTransport? transport = null;
		var registry = new StaticClusterRegistry(configuration, nodeId);
		var system = new ActorSystem(
			options: new ActorSystemOptions { ClusterRegistry = registry },
			transportFactory: sys =>
			{
				transport = new TcpTransport(sys, registry, options, NullLoggerFactory.Instance);
				return transport;
			});
		return new ChaosNode(system, transport!);
	}

	/// <summary>One wired-up node plus its transport, disposed as a unit by the tests.</summary>
	private sealed class ChaosNode(ActorSystem System, TcpTransport Transport) : IAsyncDisposable
	{
		public ActorSystem System
		{
			get;
		} = System;

		public TcpTransport Transport
		{
			get;
		} = Transport;

		public async ValueTask DisposeAsync()
		{
			await System.DisposeAsync().ConfigureAwait(false);
		}
	}

	private static StaticClusterConfiguration BuildConfiguration(params StaticClusterNodeConfiguration[] nodes)
	{
		return new StaticClusterConfiguration { Nodes = nodes };
	}

	private static StaticClusterNodeConfiguration CreateNodeConfiguration(string nodeId, int port,
		long handleOffset, params (string Service, long Handle)[] services)
	{
		return new StaticClusterNodeConfiguration
		{
			NodeId = nodeId,
			Host = "127.0.0.1",
			Port = port,
			HandleOffset = handleOffset,
			Services = services.ToDictionary(
				service => service.Service,
				service => service.Handle,
				StringComparer.Ordinal)
		};
	}

	private static int GetFreePort()
	{
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();
		return port;
	}

	private sealed class ChaosEchoActor : Actor
	{
		protected override Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
			return envelope.Payload is ChaosEchoRequest request
				? Task.FromResult<object?>("echo:" + request.Message)
				: Task.FromResult<object?>(null);
		}
	}

	private sealed class ChaosSlowActor : Actor
	{
		protected override async Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
			// Simulates a long-running handler: the response never arrives during the tests.
			// The delay honors cancellation so the actor does not stall system disposal.
			await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
			return "slow:done";
		}
	}

	private sealed class ChaosOrderingActor : Actor
	{
		private readonly List<int> _receivedIndexes = [];

		protected override Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
			switch (envelope.Payload)
			{
				case ChaosOrderRequest request:
					_receivedIndexes.Add(request.Index);
					return Task.FromResult<object?>(null);
				case ChaosGetOrderSnapshot:
					return Task.FromResult<object?>(new ChaosOrderSnapshot([.. _receivedIndexes]));
				default:
					return Task.FromResult<object?>(null);
			}
		}
	}

	[MessagePackObject(AllowPrivate = true)]
	internal sealed record ChaosEchoRequest([property: Key(0)] string Message);

	[MessagePackObject(AllowPrivate = true)]
	internal sealed record ChaosOrderRequest([property: Key(0)] int Index);

	[MessagePackObject(AllowPrivate = true)]
	internal sealed record ChaosGetOrderSnapshot;

	[MessagePackObject(AllowPrivate = true)]
	internal sealed record ChaosOrderSnapshot([property: Key(0)] int[] Indexes);
}

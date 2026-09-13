using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using Skynet.Cluster;
using Skynet.Core;
using Skynet.Core.Serialization;
using Xunit;

namespace Skynet.Core.Tests;

public sealed class TcpTransportTests
{
[Fact]
public async Task CallAsync_ShouldRoundtripAcrossNodes()
{
var (port1, port2) = (GetFreePort(), GetFreePort());
var configuration = new StaticClusterConfiguration
{
Nodes = new[]
{
new StaticClusterNodeConfiguration
{
NodeId = "node1",
Host = "127.0.0.1",
Port = port1,
HandleOffset = 1000,
Services = new Dictionary<string, long>(StringComparer.Ordinal)
{
["echo"] = 1001
		}
},
new StaticClusterNodeConfiguration
{
NodeId = "node2",
Host = "127.0.0.1",
Port = port2,
HandleOffset = 2000,
Services = new Dictionary<string, long>(StringComparer.Ordinal)
}
}
			};

var registry1 = new StaticClusterRegistry(configuration, "node1");
var registry2 = new StaticClusterRegistry(configuration, "node2");

await using var system1 = new ActorSystem(
options: new ActorSystemOptions { ClusterRegistry = registry1 },
transportFactory: sys => new TcpTransport(sys, registry1, new TcpTransportOptions
{
HeartbeatInterval = TimeSpan.FromMilliseconds(250)
}, NullLoggerFactory.Instance));

await system1.CreateActorAsync(() => new EchoActor(), "echo", new ActorCreationOptions { HandleOverride = new ActorHandle(1001) });

await using var system2 = new ActorSystem(
options: new ActorSystemOptions { ClusterRegistry = registry2 },
transportFactory: sys => new TcpTransport(sys, registry2, new TcpTransportOptions
{
HeartbeatInterval = TimeSpan.FromMilliseconds(250)
}, NullLoggerFactory.Instance));

var remote = system2.GetByName("echo");
var result = await remote.CallAsync<string>(new EchoRequest("ping"), TimeSpan.FromSeconds(5));

result.Should().Be("echo:pong");
	}

	[Fact]
	public async Task TcpTransport_ShouldRejectOversizedFrameAndDisconnectClient()
	{
		var port = GetFreePort();
		var configuration = new StaticClusterConfiguration
		{
			Nodes = new[] { CreateNodeConfiguration("node1", port, 1000) }
		};
		var registry = new StaticClusterRegistry(configuration, "node1");

		await using var system = new ActorSystem(
			options: new ActorSystemOptions { ClusterRegistry = registry },
			transportFactory: sys => new TcpTransport(sys, registry, new TcpTransportOptions
			{
				HeartbeatInterval = TimeSpan.FromSeconds(60)
			}, NullLoggerFactory.Instance));

		using var client = new TcpClient();
		await client.ConnectAsync(IPAddress.Loopback, port);
		var stream = client.GetStream();

		// Complete the handshake so the malicious frame is processed by the read loop.
		await WriteFrameAsync(stream, 0x01, MessagePackSerializer.Serialize(new HandshakePayload("node1")));
		var (handshakeType, _) = await ReadFrameAsync(stream, TimeSpan.FromSeconds(10));
		handshakeType.Should().Be(0x01);

		// Announce a frame whose payload length is int.MaxValue without sending any body.
		await WriteFrameHeaderAsync(stream, 0x02, int.MaxValue);

		// The transport must hard-reject the frame and close the connection instead of
		// attempting to allocate the announced payload.
		var readBuffer = new byte[1];
		using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		var bytesRead = await stream.ReadAsync(readBuffer, timeoutCts.Token);
		bytesRead.Should().Be(0, "the transport should close the connection after rejecting an oversized frame");
	}

	[Fact]
	public async Task CallAsync_ShouldFailPendingCallWhenRemoteNodeDisconnects()
	{
		var (port1, port2) = (GetFreePort(), GetFreePort());
		var configuration = new StaticClusterConfiguration
		{
			Nodes = new[]
			{
				CreateNodeConfiguration("node1", port1, 1000, ("slow", 1001)),
				CreateNodeConfiguration("node2", port2, 2000)
			}
		};
		var registry1 = new StaticClusterRegistry(configuration, "node1");
		var registry2 = new StaticClusterRegistry(configuration, "node2");

		TcpTransport? transport1 = null;
		await using var system1 = new ActorSystem(
			options: new ActorSystemOptions { ClusterRegistry = registry1 },
			transportFactory: sys => transport1 = new TcpTransport(sys, registry1, new TcpTransportOptions
			{
				HeartbeatInterval = TimeSpan.FromSeconds(60)
			}, NullLoggerFactory.Instance));
		await system1.CreateActorAsync(() => new SlowActor(), "slow",
			new ActorCreationOptions { HandleOverride = new ActorHandle(1001) });

		await using var system2 = new ActorSystem(
			options: new ActorSystemOptions { ClusterRegistry = registry2 },
			transportFactory: sys => new TcpTransport(sys, registry2, new TcpTransportOptions
			{
				HeartbeatInterval = TimeSpan.FromSeconds(60)
			}, NullLoggerFactory.Instance));

		var remote = system2.GetByName("slow");
		var callTask = remote.CallAsync<string>(new EchoRequest("ping"), TimeSpan.FromSeconds(30));
		await Task.Delay(TimeSpan.FromMilliseconds(500));

		// Killing the remote node must fail the pending call instead of leaving it hanging
		// until the 30 second timeout expires.
		await transport1!.DisposeAsync();

		Func<Task> act = async () => await callTask.WaitAsync(TimeSpan.FromSeconds(10));
		await act.Should().ThrowAsync<RemoteConnectionClosedException>()
			.Where(exception => exception.NodeId == "node1");
	}

	[Fact]
	public async Task CallAsync_ShouldFailPendingCallWhenSilentPeerExceedsGracePeriod()
	{
		var (port1, port2) = (GetFreePort(), GetFreePort());
		var configuration = new StaticClusterConfiguration
		{
			Nodes = new[]
			{
				CreateNodeConfiguration("node1", port1, 1000, ("slow", 1001)),
				CreateNodeConfiguration("node2", port2, 2000)
			}
		};
		var registry1 = new StaticClusterRegistry(configuration, "node1");
		var registry2 = new StaticClusterRegistry(configuration, "node2");

		await using var system1 = new ActorSystem(
			options: new ActorSystemOptions { ClusterRegistry = registry1 },
			transportFactory: sys => new TcpTransport(sys, registry1, new TcpTransportOptions
			{
				// node1 stays silent for the whole test: no responses and no heartbeats.
				HeartbeatInterval = TimeSpan.FromSeconds(60)
			}, NullLoggerFactory.Instance));
		await system1.CreateActorAsync(() => new SlowActor(), "slow",
			new ActorCreationOptions { HandleOverride = new ActorHandle(1001) });

		// node2 heartbeats aggressively and declares a silent peer dead after a short grace period.
		await using var system2 = new ActorSystem(
			options: new ActorSystemOptions { ClusterRegistry = registry2 },
			transportFactory: sys => new TcpTransport(sys, registry2, new TcpTransportOptions
			{
				HeartbeatInterval = TimeSpan.FromMilliseconds(50),
				DeadNodeGracePeriod = TimeSpan.FromMilliseconds(300)
			}, NullLoggerFactory.Instance));

		var remote = system2.GetByName("slow");
		var callTask = remote.CallAsync<string>(new EchoRequest("ping"), TimeSpan.FromSeconds(30));

		// The silent peer never answers, so node2 must detect the half-open connection after
		// the grace period and fail the pending call without waiting for the 30 second timeout.
		Func<Task> act = async () => await callTask.WaitAsync(TimeSpan.FromSeconds(10));
		await act.Should().ThrowAsync<RemoteConnectionClosedException>()
			.Where(exception => exception.NodeId == "node1");
	}

	[Fact]
	public async Task RpcProxy_ShouldRoundtripAcrossNodes()
	{
		var (port1, port2) = (GetFreePort(), GetFreePort());
		var configuration = new StaticClusterConfiguration
		{
			Nodes = new[]
			{
				CreateNodeConfiguration("node1", port1, 1000, ("echoRpc", 1001)),
				CreateNodeConfiguration("node2", port2, 2000)
			}
		};
		var registry1 = new StaticClusterRegistry(configuration, "node1");
		var registry2 = new StaticClusterRegistry(configuration, "node2");

		await using var system1 = new ActorSystem(
			options: new ActorSystemOptions { ClusterRegistry = registry1 },
			transportFactory: sys => new TcpTransport(sys, registry1, new TcpTransportOptions
			{
				HeartbeatInterval = TimeSpan.FromMilliseconds(250)
			}, NullLoggerFactory.Instance));
		await system1.CreateActorAsync(() => new EchoRpcActor(), "echoRpc",
			new ActorCreationOptions { HandleOverride = new ActorHandle(1001) });

		await using var system2 = new ActorSystem(
			options: new ActorSystemOptions { ClusterRegistry = registry2 },
			transportFactory: sys => new TcpTransport(sys, registry2, new TcpTransportOptions
			{
				HeartbeatInterval = TimeSpan.FromMilliseconds(250)
			}, NullLoggerFactory.Instance));

		// The proxy serializes request payload types generated by the source generator; the remote
		// side must resolve them through the contract id table instead of reflection.
		var proxy = system2.GetService<IEchoRpc>("echoRpc");
		var echo = await proxy.EchoAsync("ping");
		echo.Should().Be("echo:ping");

		var info = await proxy.GetInfoAsync(new PongRequest(21));
		info.Doubled.Should().Be(42);
		info.Source.Should().Be("rpc");
	}

	[Fact]
	public async Task SendAsync_ShouldDeliverPlainObjectPayloadAcrossNodes()
	{
		var (port1, port2) = (GetFreePort(), GetFreePort());
		var configuration = new StaticClusterConfiguration
		{
			Nodes = new[]
			{
				CreateNodeConfiguration("node1", port1, 1000, ("recorder", 1001)),
				CreateNodeConfiguration("node2", port2, 2000)
			}
		};
		var registry1 = new StaticClusterRegistry(configuration, "node1");
		var registry2 = new StaticClusterRegistry(configuration, "node2");

		await using var system1 = new ActorSystem(
			options: new ActorSystemOptions { ClusterRegistry = registry1 },
			transportFactory: sys => new TcpTransport(sys, registry1, new TcpTransportOptions
			{
				HeartbeatInterval = TimeSpan.FromMilliseconds(250)
			}, NullLoggerFactory.Instance));
		await system1.CreateActorAsync(() => new RecordingActor(), "recorder",
			new ActorCreationOptions { HandleOverride = new ActorHandle(1001) });

		await using var system2 = new ActorSystem(
			options: new ActorSystemOptions { ClusterRegistry = registry2 },
			transportFactory: sys => new TcpTransport(sys, registry2, new TcpTransportOptions
			{
				HeartbeatInterval = TimeSpan.FromMilliseconds(250)
			}, NullLoggerFactory.Instance));

		var remote = system2.GetByName("recorder");
		await system2.SendAsync(remote.Handle, new EchoRequest("fire"));

		// Fire-and-forget: poll the remote actor until the plain object payload has arrived.
		for (var attempt = 0; attempt < 50; attempt++)
		{
			var last = await remote.CallAsync<string>(new GetLastMessage(), TimeSpan.FromSeconds(2));
			if (last == "fire")
			{
				return;
			}

			await Task.Delay(100);
		}

		throw new InvalidOperationException("The plain object payload was never delivered to the remote actor.");
	}

	[Fact]
	public async Task TcpTransport_ShouldKeepConnectionAliveWhenUnknownContractIdReceived()
	{
		var port = GetFreePort();
		var configuration = new StaticClusterConfiguration
		{
			Nodes = new[] { CreateNodeConfiguration("node1", port, 1000, ("echo", 1001)) }
		};
		var registry = new StaticClusterRegistry(configuration, "node1");

		await using var system = new ActorSystem(
			options: new ActorSystemOptions { ClusterRegistry = registry },
			transportFactory: sys => new TcpTransport(sys, registry, new TcpTransportOptions
			{
				HeartbeatInterval = TimeSpan.FromSeconds(60)
			}, NullLoggerFactory.Instance));
		await system.CreateActorAsync(() => new EchoActor(), "echo",
			new ActorCreationOptions { HandleOverride = new ActorHandle(1001) });

		using var client = new TcpClient();
		await client.ConnectAsync(IPAddress.Loopback, port);
		var stream = client.GetStream();
		await WriteFrameAsync(stream, 0x01, MessagePackSerializer.Serialize(new HandshakePayload("raw-node")));
		await ReadFrameAsync(stream, TimeSpan.FromSeconds(10));

		// An envelope whose payload contract id is unknown to node1 must be rejected (and logged)
		// without tearing the connection down. Use an id no other test registers.
		var unknown = new SerializedMessageEnvelope
		{
			MessageId = 1,
			From = 2,
			To = 1001,
			CallType = CallType.Send,
			PayloadContractId = 555123456,
			Payload = new byte[] { 1 },
			TraceId = null,
			Timestamp = DateTimeOffset.UtcNow.UtcTicks,
			TimeToLiveTicks = null,
			Version = MessageEnvelopeSerializer.WireVersion
		};
		await WriteFrameAsync(stream, 0x02, MessagePackSerializer.Serialize(unknown));

		// A well-formed call on the same connection must still be answered.
		var call = new SerializedMessageEnvelope
		{
			MessageId = 3,
			From = 2,
			To = 1001,
			CallType = CallType.Call,
			PayloadContractId = PayloadContractRegistry.GetOrRegister(typeof(EchoRequest)),
			Payload = MessagePackSerializer.Serialize(new EchoRequest("ping")),
			TraceId = null,
			Timestamp = DateTimeOffset.UtcNow.UtcTicks,
			TimeToLiveTicks = null,
			Version = MessageEnvelopeSerializer.WireVersion
		};
		await WriteFrameAsync(stream, 0x02, MessagePackSerializer.Serialize(call));

		var (responseType, responsePayload) = await ReadFrameAsync(stream, TimeSpan.FromSeconds(10));
		responseType.Should().Be(0x02);
		var response = MessagePackSerializer.Deserialize<SerializedMessageEnvelope>(responsePayload);
		response.PayloadContractId.Should().Be(PayloadContractRegistry.ComputeContractId("System.String"));
		PayloadContractRegistry.TryResolve(response.PayloadContractId, out var responsePayloadType).Should().BeTrue();
		MessagePackSerializer.Deserialize(responsePayloadType!, response.Payload).Should().Be("echo:pong");
	}

	[Fact]
	public async Task CallAsync_BidirectionalConcurrentCalls_ShouldNotCrossMatchPendingCalls()
	{
		// Both nodes allocate message ids from their own counters starting at the same value, so a
		// request from node2 carries the same MessageId as a pending call node1 has outstanding
		// towards node2. Pending matching must therefore never treat a request frame as a response.
		var (port1, port2) = (GetFreePort(), GetFreePort());
		var configuration = new StaticClusterConfiguration
		{
			Nodes = new[]
			{
				CreateNodeConfiguration("node1", port1, 1000, ("echo", 1001)),
				CreateNodeConfiguration("node2", port2, 2000, ("echo2", 2001))
			}
		};
		var registry1 = new StaticClusterRegistry(configuration, "node1");
		var registry2 = new StaticClusterRegistry(configuration, "node2");

		await using var system1 = new ActorSystem(
			options: new ActorSystemOptions { ClusterRegistry = registry1 },
			transportFactory: sys => new TcpTransport(sys, registry1, new TcpTransportOptions
			{
				HeartbeatInterval = TimeSpan.FromSeconds(60)
			}, NullLoggerFactory.Instance));
		await system1.CreateActorAsync(() => new EchoActor(), "echo",
			new ActorCreationOptions { HandleOverride = new ActorHandle(1001) });

		await using var system2 = new ActorSystem(
			options: new ActorSystemOptions { ClusterRegistry = registry2 },
			transportFactory: sys => new TcpTransport(sys, registry2, new TcpTransportOptions
			{
				HeartbeatInterval = TimeSpan.FromSeconds(60)
			}, NullLoggerFactory.Instance));
		await system2.CreateActorAsync(() => new EchoActor(), "echo2",
			new ActorCreationOptions { HandleOverride = new ActorHandle(2001) });

		// Warm up both directions serially so each link is established before the lockstep loop;
		// the aligned counters (one envelope per warm-up call on each side) also stay aligned.
		(await system1.CallAsync<string>(new ActorHandle(2001), new EchoRequest("warmup-1"),
			TimeSpan.FromSeconds(10))).Should().Be("echo:warmup-1");
		(await system2.CallAsync<string>(new ActorHandle(1001), new EchoRequest("warmup-2"),
			TimeSpan.FromSeconds(10))).Should().Be("echo:warmup-2");

		// Drive both directions in lockstep so the per-node message id counters stay aligned and
		// every round produces a request whose MessageId equals the peer's outstanding pending id.
		const int rounds = 100;
		var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
		for (var i = 0; i < rounds; i++)
		{
			var callNode2 = system1.CallAsync<string>(new ActorHandle(2001), new EchoRequest($"n1-{i}"),
				TimeSpan.FromSeconds(10));
			var callNode1 = system2.CallAsync<string>(new ActorHandle(1001), new EchoRequest($"n2-{i}"),
				TimeSpan.FromSeconds(10));
			try
			{
				var results = await Task.WhenAll(callNode2, callNode1);
				results[0].Should().Be($"echo:n1-{i}");
				results[1].Should().Be($"echo:n2-{i}");
			}
			catch (Exception ex)
			{
				errors.Add(ex);
				break; // One cross-match is proof enough; skip the remaining rounds.
			}
		}

		errors.Should().BeEmpty("cross-matched pending calls corrupt results and swallow requests");
	}

	[Fact]
	public async Task CallAsync_ShouldFailFastAndDrainPendingCallsWhenConnectFails()
	{
		// node2's descriptor points at a port with no listener: the connect attempt is refused.
		// The service entry is required so the target handle resolves to node2 at all.
		var (port1, unreachablePort) = (GetFreePort(), GetFreePort());
		var configuration = new StaticClusterConfiguration
		{
			Nodes = new[]
			{
				CreateNodeConfiguration("node1", port1, 1000),
				CreateNodeConfiguration("node2", unreachablePort, 2000, ("echo", 2001))
			}
		};
		var registry1 = new StaticClusterRegistry(configuration, "node1");

		TcpTransport? transport = null;
		await using var system1 = new ActorSystem(
			options: new ActorSystemOptions { ClusterRegistry = registry1 },
			transportFactory: sys => transport = new TcpTransport(sys, registry1, new TcpTransportOptions
			{
				ConnectTimeout = TimeSpan.FromSeconds(2),
				HeartbeatInterval = TimeSpan.FromSeconds(60)
			}, NullLoggerFactory.Instance));

		Func<Task> act = async () => await system1.CallAsync<string>(new ActorHandle(2001),
			new EchoRequest("ping"), TimeSpan.FromSeconds(10));

		// The call must fail within the connect timeout instead of hanging forever.
		await act.Should().ThrowAsync<Exception>().WaitAsync(TimeSpan.FromSeconds(5));

		transport.Should().NotBeNull();
		GetPendingCallCount(transport!).Should().Be(0,
			"a failed connect must fail-fast the pending call instead of leaking it");
	}

	[Fact]
	public async Task OnConnectionClosed_ShouldOnlyRemoveItsOwnRegistration()
	{
		// Deterministic white-box test for the ABA defect: the cleanup of a dead connection must
		// never remove a replacement connection that was registered concurrently (reconnect race).
		var port = GetFreePort();
		var configuration = new StaticClusterConfiguration
		{
			Nodes = new[] { CreateNodeConfiguration("node1", port, 1000) }
		};
		var registry = new StaticClusterRegistry(configuration, "node1");

		TcpTransport? transport = null;
		await using var system = new ActorSystem(
			options: new ActorSystemOptions { ClusterRegistry = registry },
			transportFactory: sys => transport = new TcpTransport(sys, registry, new TcpTransportOptions
			{
				HeartbeatInterval = TimeSpan.FromSeconds(60)
			}, NullLoggerFactory.Instance));
		transport.Should().NotBeNull();

		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var connectionA = await CreateHandshakenConnectionAsync(transport!, listener);
		var connectionB = await CreateHandshakenConnectionAsync(transport!, listener);
		try
		{
			var connections = GetConnectionDictionary(transport!);
			connections["ghost"] = connectionA;

			// The dying connection itself is still the registered link: its entry is removed.
			transport!.OnConnectionClosed(connectionA);
			connections.ContainsKey("ghost").Should().BeFalse();

			// A replacement connection has already been registered when the stale cleanup of the
			// old connection finally runs: the ABA guard must keep the replacement registered.
			connections["ghost"] = connectionB;
			transport.OnConnectionClosed(connectionA);
			connections["ghost"].Should().BeSameAs(connectionB);

			transport.OnConnectionClosed(connectionB);
			connections.ContainsKey("ghost").Should().BeFalse();
		}
		finally
		{
			await connectionA.DisposeAsync();
			await connectionB.DisposeAsync();
		}
	}

	[Fact]
	public async Task CallAsync_ShouldReconnectAfterRemoteNodeRestarts()
	{
		var (port1, port2) = (GetFreePort(), GetFreePort());
		var configuration = new StaticClusterConfiguration
		{
			Nodes = new[]
			{
				CreateNodeConfiguration("node1", port1, 1000, ("echo", 1001)),
				CreateNodeConfiguration("node2", port2, 2000)
			}
		};

		var registry2 = new StaticClusterRegistry(configuration, "node2");
		TcpTransport? transport2 = null;
		await using var system2 = new ActorSystem(
			options: new ActorSystemOptions { ClusterRegistry = registry2 },
			transportFactory: sys => transport2 = new TcpTransport(sys, registry2, new TcpTransportOptions
			{
				HeartbeatInterval = TimeSpan.FromSeconds(60)
			}, NullLoggerFactory.Instance));
		await using (var system1 = CreateTcpSystem(configuration, "node1"))
		{
			await system1.CreateActorAsync(() => new EchoActor(), "echo",
				new ActorCreationOptions { HandleOverride = new ActorHandle(1001) });
			var remote = system2.GetByName("echo");
			(await remote.CallAsync<string>(new EchoRequest("ping"), TimeSpan.FromSeconds(5)))
				.Should().Be("echo:pong");
		}

		// Wait until node2's read loop has cleaned up the dead connection so the reconnect below
		// races neither the stale cleanup nor the disposal of a connection still being written to.
		var connections = GetConnectionDictionary(transport2!);
		for (var attempt = 0; attempt < 200 && connections.Count > 0; attempt++)
		{
			await Task.Delay(25);
		}

		connections.Should().BeEmpty("the dead connection's cleanup must have completed");

		// node1 is gone; node2's registered connection is (or soon becomes) dead. Restarting node1
		// on the same port and calling again must establish a fresh connection instead of reusing
		// or corrupting the state of the dead one.
		await using var restartedSystem1 = CreateTcpSystem(configuration, "node1");
		await restartedSystem1.CreateActorAsync(() => new EchoActor(), "echo",
			new ActorCreationOptions { HandleOverride = new ActorHandle(1001) });

		var result = await system2.GetByName("echo").CallAsync<string>(new EchoRequest("ping"),
			TimeSpan.FromSeconds(10));
		result.Should().Be("echo:pong");
	}

	private static ActorSystem CreateTcpSystem(StaticClusterConfiguration configuration, string nodeId)
	{
		var registry = new StaticClusterRegistry(configuration, nodeId);
		return new ActorSystem(
			options: new ActorSystemOptions { ClusterRegistry = registry },
			transportFactory: sys => new TcpTransport(sys, registry, new TcpTransportOptions
			{
				HeartbeatInterval = TimeSpan.FromSeconds(60)
			}, NullLoggerFactory.Instance));
	}

	private static ConcurrentDictionary<string, TcpTransport.TcpConnection> GetConnectionDictionary(
		TcpTransport transport)
	{
		var field = typeof(TcpTransport).GetField("_connections", BindingFlags.NonPublic | BindingFlags.Instance)
			?? throw new InvalidOperationException("The _connections field was not found.");
		return (ConcurrentDictionary<string, TcpTransport.TcpConnection>)field.GetValue(transport)!;
	}

	/// <summary>
	/// Creates an outbound transport connection over a raw loopback listener and completes the
	/// cluster handshake so the remote node id is known. The read loop is not started.
	/// </summary>
	private static async Task<TcpTransport.TcpConnection> CreateHandshakenConnectionAsync(
		TcpTransport transport, TcpListener listener)
	{
		var client = new TcpClient();
		await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
		var connection = new TcpTransport.TcpConnection(transport, client, outbound: true,
			NullLogger.Instance, TimeSpan.Zero, TimeSpan.Zero, 0, MessagePackSerializerOptions.Standard);
		var handshake = connection.InitializeAsync("node1", CancellationToken.None);

		var server = await listener.AcceptTcpClientAsync();
		var stream = server.GetStream();
		var (type, _) = await ReadFrameAsync(stream, TimeSpan.FromSeconds(10));
		type.Should().Be(0x01);
		await WriteFrameAsync(stream, 0x01,
			MessagePackSerializer.Serialize(new TcpTransport.ClusterHandshake("ghost")));
		await handshake;
		return connection;
	}

	private static int GetPendingCallCount(TcpTransport transport)
	{
		var field = typeof(TcpTransport).GetField("_pendingCalls", BindingFlags.NonPublic | BindingFlags.Instance)
			?? throw new InvalidOperationException("The _pendingCalls field was not found.");
		var pendingCalls = (System.Collections.IDictionary)field.GetValue(transport)!;
		return pendingCalls.Count;
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

	private static async Task WriteFrameAsync(NetworkStream stream, byte type, byte[] payload)
	{
		var frame = new byte[5 + payload.Length];
		frame[0] = type;
		BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payload.Length)).CopyTo(frame, 1);
		payload.CopyTo(frame, 5);
		await stream.WriteAsync(frame);
	}

	private static async Task WriteFrameHeaderAsync(NetworkStream stream, byte type, int payloadLength)
	{
		var header = new byte[5];
		header[0] = type;
		BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payloadLength)).CopyTo(header, 1);
		await stream.WriteAsync(header);
		await stream.FlushAsync();
	}

	private static async Task<(byte Type, byte[] Payload)> ReadFrameAsync(NetworkStream stream, TimeSpan timeout)
	{
		using var cts = new CancellationTokenSource(timeout);
		var typeBuffer = new byte[1];
		await ReadExactAsync(stream, typeBuffer, cts.Token);
		var lengthBuffer = new byte[4];
		await ReadExactAsync(stream, lengthBuffer, cts.Token);
		var length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lengthBuffer, 0));
		var payload = new byte[Math.Max(length, 0)];
		if (length > 0)
		{
			await ReadExactAsync(stream, payload, cts.Token);
		}

		return (typeBuffer[0], payload);
	}

	private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
	{
		var offset = 0;
		while (offset < buffer.Length)
		{
			var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
			if (read == 0)
			{
				throw new IOException("Connection closed while reading frame data.");
			}

			offset += read;
		}
	}

	private static int GetFreePort()
	{
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();
		return port;
	}

	private sealed class EchoActor : Actor
	{
		protected override Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
			return envelope.Payload switch
			{
				EchoRequest request => Task.FromResult<object?>("echo:" + request.Message.Replace("ping", "pong", StringComparison.Ordinal)),
				_ => Task.FromResult<object?>(null)
			};
		}
	}

	private sealed class SlowActor : Actor
	{
		protected override async Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
			// Simulates a long-running handler: the response never arrives during the tests.
			// The delay honors cancellation so the actor does not stall system disposal.
			await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
			return "slow:done";
		}
	}

	private sealed class RecordingActor : Actor
	{
		private string? _lastMessage;

		protected override Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
			switch (envelope.Payload)
			{
				case EchoRequest request:
					_lastMessage = request.Message;
					return Task.FromResult<object?>(null);
				case GetLastMessage:
					return Task.FromResult<object?>(_lastMessage);
				default:
					return Task.FromResult<object?>(null);
			}
		}
	}

	private sealed class EchoRpcActor : RpcActor<IEchoRpc>, IEchoRpc
	{
		public Task<string> EchoAsync(string message, CancellationToken cancellationToken = default)
		{
			return Task.FromResult("echo:" + message);
		}

		public Task<PongResponse> GetInfoAsync(PongRequest request, CancellationToken cancellationToken = default)
		{
			return Task.FromResult(new PongResponse(request.Value * 2, "rpc"));
		}
	}

	[MessagePackObject(AllowPrivate = true)]
	internal sealed record GetLastMessage;

	[MessagePackObject(AllowPrivate = true)]
	internal sealed record EchoRequest([property: Key(0)] string Message);

	[MessagePackObject(AllowPrivate = true)]
	internal sealed record HandshakePayload([property: Key(0)] string NodeId);
}

[SkynetActor("echoRpc")]
public interface IEchoRpc
{
	Task<string> EchoAsync(string message, CancellationToken cancellationToken = default);

	Task<PongResponse> GetInfoAsync(PongRequest request, CancellationToken cancellationToken = default);
}

[MessagePackObject]
public sealed record PongRequest([property: Key(0)] int Value);

[MessagePackObject]
public sealed record PongResponse([property: Key(0)] int Doubled, [property: Key(1)] string Source);

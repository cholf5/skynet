using System.Net;
using System.Net.Sockets;
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

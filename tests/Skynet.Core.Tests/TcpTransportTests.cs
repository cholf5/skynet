using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using FluentAssertions;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using Skynet.Cluster;
using Skynet.Core;
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

	[MessagePackObject(AllowPrivate = true)]
internal sealed record EchoRequest([property: Key(0)] string Message);
}

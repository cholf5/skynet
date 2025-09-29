using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using Skynet.Cluster;
using Skynet.Core;

namespace Skynet.Examples;

public static class Program
{
public static async Task Main(string[] args)
{
if (args.Length >= 2 && string.Equals(args[0], "--cluster", StringComparison.OrdinalIgnoreCase))
{
await RunClusterSampleAsync(args[1]).ConfigureAwait(false);
return;
}

await RunLocalSampleAsync().ConfigureAwait(false);
}

private static async Task RunLocalSampleAsync()
{
Console.WriteLine("Bootstrapping Skynet runtime with generated login proxy...");
await using var system = new ActorSystem();
await system.CreateActorAsync(() => new LoginActor(), "login");
var login = system.GetService<ILoginActor>("login");
var welcome = await login.LoginAsync(new LoginRequest("demo", "password"));
Console.WriteLine($"Login => {welcome.WelcomeMessage}");
await login.NotifyAsync(new LoginNotice(welcome.Username, "connected"));
Console.WriteLine($"Ping => {login.Ping(welcome.Username)}");

var echo = await system.CreateActorAsync(() => new EchoActor(), "echo");
Console.WriteLine("Echo actor registered as 'echo'. Type messages to interact. Press ENTER on an empty line to exit.");

while (true)
{
Console.Write("> ");
var line = Console.ReadLine();
if (string.IsNullOrWhiteSpace(line))
{
break;
}

await echo.SendAsync(new EchoNotice(line));
var response = await echo.CallAsync<string>(new EchoRequest(line));
Console.WriteLine($"[reply] {response}");
}

Console.WriteLine("Shutting down actor system...");
}

private static async Task RunClusterSampleAsync(string nodeId)
{
var configuration = new StaticClusterConfiguration
{
Nodes = new[]
{
new StaticClusterNodeConfiguration
{
NodeId = "node1",
Host = "127.0.0.1",
Port = 9101,
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
Port = 9102,
HandleOffset = 2000,
Services = new Dictionary<string, long>(StringComparer.Ordinal)
}
}
};

var registry = new StaticClusterRegistry(configuration, nodeId);
var options = new ActorSystemOptions { ClusterRegistry = registry };
await using var system = new ActorSystem(
options: options,
transportFactory: sys => new TcpTransport(sys, registry, new TcpTransportOptions
{
HeartbeatInterval = TimeSpan.FromSeconds(5)
}, NullLoggerFactory.Instance));

if (string.Equals(nodeId, "node1", StringComparison.OrdinalIgnoreCase))
{
await system.CreateActorAsync(() => new EchoActor(), "echo", new ActorCreationOptions
{
HandleOverride = new ActorHandle(1001)
});
Console.WriteLine("Node1 listening on 127.0.0.1:9101. Press ENTER to exit.");
Console.ReadLine();
return;
}

var remote = system.GetByName("echo");
Console.WriteLine("Node2 connected to node1 via TCP. Type messages to call the remote echo actor. Empty line exits.");
while (true)
{
Console.Write("> ");
var line = Console.ReadLine();
if (string.IsNullOrWhiteSpace(line))
{
break;
}

var reply = await remote.CallAsync<string>(new EchoRequest(line), TimeSpan.FromSeconds(5));
Console.WriteLine($"[remote] {reply}");
}
}

	private sealed class EchoActor : Actor
	{
		protected override Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
			switch (envelope.Payload)
			{
				case EchoNotice notice:
					Console.WriteLine($"[send] {notice.Message}");
					return Task.FromResult<object?>(null);
				case EchoRequest request:
					Console.WriteLine($"[call] {request.Message}");
					return Task.FromResult<object?>(request.Message);
				default:
					throw new InvalidOperationException($"Unknown message type {envelope.Payload?.GetType().Name}.");
			}
		}
	}

	private sealed record EchoNotice(string Message);

	private sealed record EchoRequest(string Message);

	[SkynetActor("login", Unique = true)]
	public interface ILoginActor
	{
		Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
		ValueTask NotifyAsync(LoginNotice notice);
		string Ping(string name);
	}

	private sealed class LoginActor : RpcActor<ILoginActor>, ILoginActor
	{
		public Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
		{
			return Task.FromResult(new LoginResponse(request.Username, $"Welcome {request.Username}!"));
		}

		public ValueTask NotifyAsync(LoginNotice notice)
		{
			Console.WriteLine($"[login-notify] {notice.Username}: {notice.Message}");
			return ValueTask.CompletedTask;
		}

		public string Ping(string name)
		{
			return $"PONG: {name}";
		}
	}

	[MessagePackObject]
	public sealed record LoginRequest([property: Key(0)] string Username, [property: Key(1)] string Password);

	[MessagePackObject]
	public sealed record LoginResponse([property: Key(0)] string Username, [property: Key(1)] string WelcomeMessage);

	[MessagePackObject]
	public sealed record LoginNotice([property: Key(0)] string Username, [property: Key(1)] string Message);
}

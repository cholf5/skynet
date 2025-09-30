using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using Skynet.Cluster;
using Skynet.Core;
using Skynet.Extras;
using Skynet.Net;

namespace Skynet.Examples;

public static class Program
{
	public static async Task Main(string[] args)
	{
		if (args.Length >= 2 && string.Equals(args[0], "--cluster", StringComparison.OrdinalIgnoreCase))
		{
			await RunClusterSampleAsync(args[1]).CAF();
			return;
		}

		if (args.Length >= 1)
		{
			switch (args[0].ToLowerInvariant())
			{
				case "--gate":
				case "--rooms":
				await RunRoomSampleAsync().CAF();
				return;
				case "--debug-console":
				await RunDebugConsoleSampleAsync().CAF();
				return;
				case "--rooms-bench":
				await RunRoomBenchmarkAsync().CAF();
				return;
				case "--cluster" when args.Length >= 2:
				break;
				default:
				break;
			}
		}

		await RunLocalSampleAsync().CAF();
	}

	private static async Task RunLocalSampleAsync()
	{
		Console.WriteLine("Bootstrapping Skynet runtime with generated login proxy...");
		await using var system = new ActorSystem();
		await system.CreateActorAsync(() => new LoginActor(), "login").CAF();
		var login = system.GetService<ILoginActor>("login");
		var welcome = await login.LoginAsync(new LoginRequest("demo", "password")).CAF();
		Console.WriteLine($"Login => {welcome.WelcomeMessage}");
		await login.NotifyAsync(new LoginNotice(welcome.Username, "connected")).CAF();
		Console.WriteLine($"Ping => {login.Ping(welcome.Username)}");

		var echo = await system.CreateActorAsync(() => new EchoActor(), "echo").CAF();
		Console.WriteLine("Echo actor registered as 'echo'. Type messages to interact. Press ENTER on an empty line to exit.");

		while (true)
		{
			Console.Write("> ");
			var line = Console.ReadLine();
			if (string.IsNullOrWhiteSpace(line))
			{
				break;
			}

			await echo.SendAsync(new EchoNotice(line)).CAF();
			var response = await echo.CallAsync<string>(new EchoRequest(line)).CAF();
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
					HandleOffset = 2000
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
			}).CAF();
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

			var reply = await remote.CallAsync<string>(new EchoRequest(line), TimeSpan.FromSeconds(5)).CAF();
			Console.WriteLine($"[remote] {reply}");
		}
	}


	private static async Task RunDebugConsoleSampleAsync()
	{
		Console.WriteLine("Starting actor system with debug console on 127.0.0.1:4015...");
		await using var system = new ActorSystem();
		await system.CreateActorAsync(() => new EchoActor(), "echo").CAF();
		var gateway = new ActorSystemDebugConsoleGateway(system);
		var options = new DebugConsoleOptions
		{
			Host = "127.0.0.1",
			Port = 4015
		};

		await using var console = new DebugConsoleServer(gateway, options);
		await console.StartAsync().CAF();

		Console.WriteLine("Connect with `telnet 127.0.0.1 4015` and type 'help' to inspect actors.");
		Console.WriteLine("Press ENTER to stop the console.");
		Console.ReadLine();

		await console.StopAsync().CAF();
	}

	private static async Task RunRoomSampleAsync()
	{
		Console.WriteLine("Starting gate server with room management...");
		await using var system = new ActorSystem();
		var manager = new RoomManager(system);
		var options = new GateServerOptions
		{
			TcpPort = 4010,
			WebSocketPort = 4011,
			RouterFactory = context => new RoomSessionRouter(manager)
		};

		await using var gate = new GateServer(system, options, NullLogger<GateServer>.Instance);
		await gate.StartAsync().CAF();

		Console.WriteLine($"TCP clients: connect to {gate.TcpEndpoint}");
		Console.WriteLine($"WebSocket clients: connect to {gate.WebSocketEndpoint}");
		Console.WriteLine("Commands: join <room>, leave <room>, say <room> <message>, rooms, who <room>, nick <alias>.");
		Console.WriteLine("Press ENTER to stop the gate server.");
		Console.ReadLine();

		Console.WriteLine("Stopping gate server...");
		await gate.StopAsync().CAF();
	}

	private static async Task RunRoomBenchmarkAsync()
	{
		const int sessionCount = 200;
		const int iterations = 1000;
		Console.WriteLine($"Running broadcast benchmark with {sessionCount} simulated sessions and {iterations} rounds...");

		await using var system = new ActorSystem();
		var manager = new RoomManager(system);
		var actors = new List<RoomLoopbackActor>(sessionCount);

		for (var i = 0; i < sessionCount; i++)
		{
			var loopback = new RoomLoopbackActor();
			actors.Add(loopback);
			var actor = await system.CreateActorAsync(() => loopback).CAF();
			var metadata = new SessionMetadata($"bench-{i}", "loop", null, DateTimeOffset.UtcNow);
			manager.Join("load-test", new RoomParticipant(actor.Handle, metadata));
		}

		var payload = Encoding.UTF8.GetBytes("benchmark");
		var stopwatch = Stopwatch.StartNew();
		for (var i = 0; i < iterations; i++)
		{
			await manager.BroadcastAsync("load-test", payload).CAF();
		}
		stopwatch.Stop();

		var totalMessages = sessionCount * iterations;
		var throughput = totalMessages / stopwatch.Elapsed.TotalSeconds;
		Console.WriteLine($"Delivered {totalMessages} messages in {stopwatch.Elapsed.TotalMilliseconds:F2} ms.");
		Console.WriteLine($"Throughput: {throughput:F2} messages/sec");
		var perSession = actors.Count > 0 ? actors[0].Received : 0;
		Console.WriteLine($"Per-session received: {perSession}");
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

	private sealed class RoomLoopbackActor : Actor
	{
		private long _received;

		public long Received => Interlocked.Read(ref _received);

		protected override Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
			if (envelope.Payload is SessionOutboundMessage)
			{
				Interlocked.Increment(ref _received);
			}

			return Task.FromResult<object?>(null);
		}
	}

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

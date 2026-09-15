using System.Diagnostics;
using System.Net;
using System.Text;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using Skynet.Cluster;
using Skynet.Core;
using Skynet.Core.Serialization;
using Skynet.Extras;
using Skynet.Net;

namespace Skynet.Examples;

public static class Program
{
	public static async Task Main(string[] args)
	{
		if (args.Length >= 2 && string.Equals(args[0], "--cluster", StringComparison.OrdinalIgnoreCase))
		{
			await RunClusterSampleAsync(args[1]).Caf();
			return;
		}

		if (args.Length >= 1)
		{
			switch (args[0].ToLowerInvariant())
			{
				case "--gate":
				case "--rooms":
					await RunRoomSampleAsync().Caf();
					return;
				case "--debug-console":
					await RunDebugConsoleSampleAsync().Caf();
					return;
				case "--rooms-bench":
					await RunRoomBenchmarkAsync().Caf();
					return;
				case "--cluster" when args.Length >= 2:
					break;
			}
		}

		await RunLocalSampleAsync().Caf();
	}

	private static async Task RunLocalSampleAsync()
	{
		Console.WriteLine("Bootstrapping Skynet runtime with generated login proxy...");
		// Create an actor system and register a login actor
		await using var system = new ActorSystem();
		await system.CreateActorAsync(() => new LoginActor(), "login").Caf();
		// Interact with the login actor via the generated proxy interface
		var login = system.GetService<ILoginActor>("login");
		var welcome = await login.LoginAsync(new LoginRequest("demo", "password")).Caf();
		Console.WriteLine($"Login => {welcome.WelcomeMessage}");
		await login.NotifyAsync(new LoginNotice(welcome.Username, "connected")).Caf();
		Console.WriteLine($"Ping => {await login.PingAsync(welcome.Username).Caf()}");

		var echo = await system.CreateActorAsync(() => new EchoActor(), "echo").Caf();
		Console.WriteLine(
			"Echo actor registered as 'echo'. Type messages to interact. Press ENTER on an empty line to exit.");

		while (true)
		{
			Console.Write("> ");
			var line = Console.ReadLine();
			if (string.IsNullOrWhiteSpace(line))
			{
				break;
			}

			await echo.SendAsync(new EchoNotice(line)).Caf();
			var response = await echo.CallAsync<string>(new EchoRequest(line)).Caf();
			Console.WriteLine($"[reply] {response}");
		}

		Console.WriteLine("Shutting down actor system...");
	}

	private static async Task RunClusterSampleAsync(string nodeId)
	{
		// The sample exchanges payload types that have no generated [SkynetActor] contract behind
		// them. Senders self-register on first send, but receivers never invent types, so both
		// nodes must pre-register the contract ids before the first remote call can be decoded.
		PayloadContractRegistry.Register<EchoRequest>();
		PayloadContractRegistry.Register<EchoNotice>();

		var configuration = ApplyHostOverrides(BuildClusterConfiguration(includeGateNode: false));
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
			}).Caf();
			var host = configuration.Nodes.First(node =>
				string.Equals(node.NodeId, "node1", StringComparison.Ordinal)).Host;
			Console.WriteLine($"Node1 listening on {host}:9101. Press ENTER to exit.");
			await WaitForStopAsync().Caf();
			return;
		}

		var remote = system.GetByName("echo");
		Console.WriteLine(
			"Node2 connected to node1 via TCP. Type messages to call the remote echo actor. Empty line exits.");
		if (Console.IsInputRedirected)
		{
			// Non-interactive mode (detached containers): stdin is already at EOF, so probe the
			// remote echo actor periodically to keep the cross-node round trip visible in logs.
			while (true)
			{
				await Task.Delay(TimeSpan.FromSeconds(10)).Caf();
				try
				{
					var reply = await remote.CallAsync<string>(new EchoRequest("probe"), TimeSpan.FromSeconds(5)).Caf();
					Console.WriteLine($"[remote] {reply}");
				}
				catch (Exception ex)
				{
					Console.WriteLine($"[remote] probe failed: {ex.Message}");
				}
			}
		}

		while (true)
		{
			Console.Write("> ");
			var line = Console.ReadLine();
			if (string.IsNullOrWhiteSpace(line))
			{
				break;
			}

			var reply = await remote.CallAsync<string>(new EchoRequest(line), TimeSpan.FromSeconds(5)).Caf();
			Console.WriteLine($"[remote] {reply}");
		}
	}

	private static StaticClusterConfiguration BuildClusterConfiguration(bool includeGateNode)
	{
		var nodes = new List<StaticClusterNodeConfiguration>
		{
			new()
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
			new()
			{
				NodeId = "node2",
				Host = "127.0.0.1",
				Port = 9102,
				HandleOffset = 2000
			}
		};

		if (includeGateNode)
		{
			nodes.Add(new StaticClusterNodeConfiguration
			{
				NodeId = "gate",
				Host = "127.0.0.1",
				Port = 9103,
				HandleOffset = 3000
			});
		}

		return new StaticClusterConfiguration { Nodes = nodes };
	}

	/// <summary>
	/// Overrides static registry hosts from the environment. For every configured node the
	/// variable <c>SKYNET_NODE_{ID}_HOST</c> (node id upper-cased, '-' replaced by '_') replaces
	/// the default 127.0.0.1 host; container deployments set it to the Docker service name so
	/// peers discover each other across containers.
	/// </summary>
	private static StaticClusterConfiguration ApplyHostOverrides(StaticClusterConfiguration configuration)
	{
		var nodes = new List<StaticClusterNodeConfiguration>();
		foreach (var node in configuration.Nodes)
		{
			var variable = $"SKYNET_NODE_{node.NodeId.ToUpperInvariant().Replace('-', '_')}_HOST";
			var host = Environment.GetEnvironmentVariable(variable);
			if (string.IsNullOrWhiteSpace(host))
			{
				nodes.Add(node);
				continue;
			}

			Console.WriteLine($"Cluster host override: {node.NodeId} => {host.Trim()} ({variable}).");
			nodes.Add(new StaticClusterNodeConfiguration
			{
				NodeId = node.NodeId,
				Host = host.Trim(),
				Port = node.Port,
				HandleOffset = node.HandleOffset,
				Services = node.Services
			});
		}

		return new StaticClusterConfiguration { Nodes = nodes };
	}

	private static async Task WaitForStopAsync()
	{
		if (Console.IsInputRedirected)
		{
			// Detached containers (docker compose up -d) get an EOF'd stdin; Console.ReadLine would
			// return immediately and stop the sample, so block until the runtime is stopped instead.
			await Task.Delay(Timeout.InfiniteTimeSpan).Caf();
			return;
		}

		Console.ReadLine();
	}


	private static async Task RunDebugConsoleSampleAsync()
	{
		Console.WriteLine("Starting actor system with debug console on 127.0.0.1:4015...");
		await using var system = new ActorSystem();
		await system.CreateActorAsync(() => new EchoActor(), "echo").Caf();
		var gateway = new ActorSystemDebugConsoleGateway(system);
		var options = new DebugConsoleOptions
		{
			Host = "127.0.0.1",
			Port = 4015
		};

		await using var console = new DebugConsoleServer(gateway, options);
		await console.StartAsync().Caf();

		Console.WriteLine("Connect with `telnet 127.0.0.1 4015` and type 'help' to inspect actors.");
		Console.WriteLine("Press ENTER to stop the console.");
		Console.ReadLine();

		await console.StopAsync().Caf();
	}

	private static async Task RunRoomSampleAsync()
	{
		Console.WriteLine("Starting gate server with room management...");
		var clusterNodeId = Environment.GetEnvironmentVariable("SKYNET_CLUSTER_NODE_ID");
		await using var system = CreateGateActorSystem(clusterNodeId);
		var manager = new RoomManager(system);
		var options = new GateServerOptions
		{
			TcpPort = ReadPortEnvironment("SKYNET_GATE_TCP_PORT", 4010),
			WebSocketPort = ReadPortEnvironment("SKYNET_GATE_WS_PORT", 4011),
			RouterFactory = context => new RoomSessionRouter(manager)
		};

		if (IsTruthyEnvironment("SKYNET_GATE_BIND_ALL"))
		{
			// Container deployments must bind every interface instead of loopback; HttpListener
			// uses '+' as the wildcard host and PublicWebSocketHost keeps the printed endpoint
			// host-friendly for clients.
			options.TcpAddress = IPAddress.Any;
			options.WebSocketHost = "+";
			options.PublicWebSocketHost = "localhost";
		}

		await using var gate = new GateServer(system, options, NullLogger<GateServer>.Instance);
		await gate.StartAsync().Caf();

		Console.WriteLine($"TCP clients: connect to {gate.TcpEndpoint}");
		Console.WriteLine($"WebSocket clients: connect to {gate.WebSocketEndpoint}");
		if (!string.IsNullOrWhiteSpace(clusterNodeId))
		{
			Console.WriteLine($"Gate joined the sample cluster as node '{clusterNodeId}'.");
			await WarmUpClusterLinkAsync(system).Caf();
		}

		Console.WriteLine(
			"Commands: join <room>, leave <room>, say <room> <message>, rooms, who <room>, nick <alias>.");
		Console.WriteLine("Press ENTER to stop the gate server.");
		await WaitForStopAsync().Caf();

		Console.WriteLine("Stopping gate server...");
		await gate.StopAsync().Caf();
	}

	private static ActorSystem CreateGateActorSystem(string? clusterNodeId)
	{
		if (string.IsNullOrWhiteSpace(clusterNodeId))
		{
			return new ActorSystem();
		}

		// Joining the sample cluster turns the gate into a third static node, so the transport
		// heartbeats to node1/node2 and cluster-wide actors remain resolvable from the gate.
		var configuration = ApplyHostOverrides(BuildClusterConfiguration(includeGateNode: true));
		var registry = new StaticClusterRegistry(configuration, clusterNodeId);
		var options = new ActorSystemOptions { ClusterRegistry = registry };
		return new ActorSystem(
			options: options,
			transportFactory: sys => new TcpTransport(sys, registry, new TcpTransportOptions
			{
				HeartbeatInterval = TimeSpan.FromSeconds(5)
			}, NullLoggerFactory.Instance));
	}

	/// <summary>
	/// Transport links are established lazily on first send, so a single warm-up call right after
	/// startup makes the gate's cluster membership immediately visible in the logs of both sides
	/// and keeps the heartbeat link to the echo node open.
	/// </summary>
	private static async Task WarmUpClusterLinkAsync(ActorSystem system)
	{
		try
		{
			var echo = system.GetByName("echo");
			var reply = await echo.CallAsync<string>(new EchoRequest("gate-handshake"), TimeSpan.FromSeconds(5)).Caf();
			Console.WriteLine($"Cluster warm-up: echo actor replied '{reply}'.");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Cluster warm-up failed (cluster peers may be offline): {ex.Message}");
		}
	}

	private static int ReadPortEnvironment(string variable, int fallback)
	{
		var raw = Environment.GetEnvironmentVariable(variable);
		if (string.IsNullOrWhiteSpace(raw))
		{
			return fallback;
		}

		if (!int.TryParse(raw, out var port) || port is < 0 or > 65535)
		{
			throw new InvalidOperationException(
				$"Environment variable {variable} must be a TCP port between 0 and 65535.");
		}

		return port;
	}

	private static bool IsTruthyEnvironment(string variable)
	{
		var raw = Environment.GetEnvironmentVariable(variable);
		return raw is not null && (raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase));
	}

	private static async Task RunRoomBenchmarkAsync()
	{
		const int sessionCount = 200;
		const int iterations = 1000;
		Console.WriteLine(
			$"Running broadcast benchmark with {sessionCount} simulated sessions and {iterations} rounds...");

		await using var system = new ActorSystem();
		var manager = new RoomManager(system);
		var actors = new List<RoomLoopbackActor>(sessionCount);

		for (var i = 0; i < sessionCount; i++)
		{
			var loopback = new RoomLoopbackActor();
			actors.Add(loopback);
			var actor = await system.CreateActorAsync(() => loopback).Caf();
			var metadata = new SessionMetadata($"bench-{i}", "loop", null, DateTimeOffset.UtcNow);
			manager.Join("load-test", new RoomParticipant(actor.Handle, metadata));
		}

		var payload = Encoding.UTF8.GetBytes("benchmark");
		var stopwatch = Stopwatch.StartNew();
		for (var i = 0; i < iterations; i++)
		{
			await manager.BroadcastAsync("load-test", payload).Caf();
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

	// Cross-node payloads must carry a MessagePack contract: MessagePack 3.x no longer emits
	// dynamic formatters for unannotated types (FormatterNotRegisteredException). At least
	// internal visibility is required by MsgPack012; AllowPrivate is recommended by MsgPack015.
	[MessagePackObject(AllowPrivate = true)]
	internal sealed record EchoNotice([property: Key(0)] string Message);

	[MessagePackObject(AllowPrivate = true)]
	internal sealed record EchoRequest([property: Key(0)] string Message);

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
		Task<string> PingAsync(string name);
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

		public Task<string> PingAsync(string name)
		{
			return Task.FromResult($"PONG: {name}");
		}
	}

	[MessagePackObject]
	public sealed record LoginRequest([property: Key(0)] string Username, [property: Key(1)] string Password);

	[MessagePackObject]
	public sealed record LoginResponse([property: Key(0)] string Username, [property: Key(1)] string WelcomeMessage);

	[MessagePackObject]
	public sealed record LoginNotice([property: Key(0)] string Username, [property: Key(1)] string Message);
}

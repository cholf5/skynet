using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using Skynet.Cluster;
using Skynet.Core;
using Skynet.Net;

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

		if (args.Length >= 1 && string.Equals(args[0], "--gate", StringComparison.OrdinalIgnoreCase))
		{
			await RunGateSampleAsync().ConfigureAwait(false);
			return;
		}

		await RunLocalSampleAsync().ConfigureAwait(false);
	}

	private static async Task RunLocalSampleAsync()
	{
		Console.WriteLine("Bootstrapping Skynet runtime with generated login proxy...");
		await using var system = new ActorSystem();
		await system.CreateActorAsync(() => new LoginActor(), "login").ConfigureAwait(false);
		var login = system.GetService<ILoginActor>("login");
		var welcome = await login.LoginAsync(new LoginRequest("demo", "password")).ConfigureAwait(false);
		Console.WriteLine($"Login => {welcome.WelcomeMessage}");
		await login.NotifyAsync(new LoginNotice(welcome.Username, "connected")).ConfigureAwait(false);
		Console.WriteLine($"Ping => {login.Ping(welcome.Username)}");

		var echo = await system.CreateActorAsync(() => new EchoActor(), "echo").ConfigureAwait(false);
		Console.WriteLine("Echo actor registered as 'echo'. Type messages to interact. Press ENTER on an empty line to exit.");

		while (true)
		{
			Console.Write("> ");
			var line = Console.ReadLine();
			if (string.IsNullOrWhiteSpace(line))
			{
				break;
			}

			await echo.SendAsync(new EchoNotice(line)).ConfigureAwait(false);
			var response = await echo.CallAsync<string>(new EchoRequest(line)).ConfigureAwait(false);
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
			}).ConfigureAwait(false);
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

			var reply = await remote.CallAsync<string>(new EchoRequest(line), TimeSpan.FromSeconds(5)).ConfigureAwait(false);
			Console.WriteLine($"[remote] {reply}");
		}
	}

	private static async Task RunGateSampleAsync()
	{
		Console.WriteLine("Starting gate server with Echo actor...");
		await using var system = new ActorSystem();
		var echo = await system.CreateActorAsync(() => new EchoActor(), "echo").ConfigureAwait(false);
		var options = new GateServerOptions
		{
			TcpPort = 4001,
			WebSocketPort = 4002,
			RouterFactory = context => new EchoSessionRouter(echo.Handle)
		};

		await using var gate = new GateServer(system, options, NullLogger<GateServer>.Instance);
		await gate.StartAsync().ConfigureAwait(false);

		Console.WriteLine($"TCP clients: connect to {gate.TcpEndpoint}");
		Console.WriteLine($"WebSocket clients: connect to {gate.WebSocketEndpoint}");
		Console.WriteLine("Press ENTER to stop the gate server.");
		Console.ReadLine();

		Console.WriteLine("Stopping gate server...");
		await gate.StopAsync().ConfigureAwait(false);
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

	private sealed class EchoSessionRouter : ISessionMessageRouter
	{
		private readonly ActorHandle _echo;

		public EchoSessionRouter(ActorHandle echo)
		{
			_echo = echo;
		}

		public Task OnSessionStartedAsync(SessionContext context, CancellationToken cancellationToken)
		{
			Console.WriteLine($"[session] connected: {context.Metadata.SessionId} ({context.Metadata.Protocol})");
			return Task.CompletedTask;
		}

		public async Task OnSessionMessageAsync(SessionContext context, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
		{
			var text = Encoding.UTF8.GetString(payload.Span);
			Console.WriteLine($"[session] inbound '{text}'");
			var reply = await context.CallAsync<string>(_echo, new EchoRequest(text), cancellationToken: cancellationToken).ConfigureAwait(false);
			var bytes = Encoding.UTF8.GetBytes(reply);
			await context.SendAsync(bytes, cancellationToken).ConfigureAwait(false);
		}

		public Task OnSessionClosedAsync(SessionContext context, SessionCloseReason reason, string? description, CancellationToken cancellationToken)
		{
			Console.WriteLine($"[session] closed: {context.Metadata.SessionId} ({reason}) {description}");
			return Task.CompletedTask;
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

using System.Threading;
using System.Threading.Tasks;
using MessagePack;
using Skynet.Core;

namespace Skynet.Examples;

public static class Program
{
	public static async Task Main(string[] args)
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

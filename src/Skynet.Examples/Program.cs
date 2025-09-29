using System.Threading;
using System.Threading.Tasks;
using Skynet.Core;

namespace Skynet.Examples;

public static class Program
{
	public static async Task Main(string[] args)
	{
		Console.WriteLine("Bootstrapping Skynet in-process echo sample...");
		await using var system = new ActorSystem();
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
}

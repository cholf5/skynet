using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Skynet.Core;
using Xunit;

namespace Skynet.Net.Tests;

public sealed class GateServerTests
{
	[Fact]
	public async Task TcpGateDeliversRoundtrip()
	{
		await using var system = new ActorSystem();
		var echo = await system.CreateActorAsync(() => new EchoActor()).ConfigureAwait(false);
		var routerSource = new TaskCompletionSource<TestSessionRouter>(TaskCreationOptions.RunContinuationsAsynchronously);
		var options = new GateServerOptions
		{
			TcpPort = 0,
			EnableWebSockets = false,
			RouterFactory = context =>
			{
				var router = new TestSessionRouter(echo.Handle);
				routerSource.TrySetResult(router);
				return router;
			}
		};

		await using var gate = new GateServer(system, options, NullLogger<GateServer>.Instance);
		await gate.StartAsync().ConfigureAwait(false);
		var endpoint = gate.TcpEndpoint.Should().NotBeNull().Subject;

		await using var client = new TcpClient();
		await client.ConnectAsync(endpoint!.Address, endpoint.Port).ConfigureAwait(false);
		await using var stream = client.GetStream();

		await WriteFrameAsync(stream, "hello").ConfigureAwait(false);
		var response = await ReadFrameAsync(stream).ConfigureAwait(false);
		response.Should().Be("HELLO");

		client.Close();

		var router = await routerSource.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
		var closed = await router.Closed.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
		closed.Reason.Should().Be(SessionCloseReason.ClientDisconnected);

		await gate.StopAsync().ConfigureAwait(false);
	}

	[Fact]
	public async Task WebSocketGateDeliversRoundtrip()
	{
		await using var system = new ActorSystem();
		var echo = await system.CreateActorAsync(() => new EchoActor()).ConfigureAwait(false);
		var routerSource = new TaskCompletionSource<TestSessionRouter>(TaskCreationOptions.RunContinuationsAsynchronously);
		var port = GetFreeTcpPort();
		var options = new GateServerOptions
		{
			EnableTcp = false,
			WebSocketHost = "localhost",
			WebSocketPort = port,
			RouterFactory = context =>
			{
				var router = new TestSessionRouter(echo.Handle);
				routerSource.TrySetResult(router);
				return router;
			}
		};

		await using var gate = new GateServer(system, options, NullLogger<GateServer>.Instance);
		await gate.StartAsync().ConfigureAwait(false);
		var uri = gate.WebSocketEndpoint.Should().NotBeNull().Subject;

		await using var client = new ClientWebSocket();
		await client.ConnectAsync(uri!, CancellationToken.None).ConfigureAwait(false);

		await client.SendAsync(Encoding.UTF8.GetBytes("ping"), WebSocketMessageType.Binary, true, CancellationToken.None).ConfigureAwait(false);
		var buffer = new byte[64];
		var result = await client.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
		var text = Encoding.UTF8.GetString(buffer.AsSpan(0, result.Count));
		text.Should().Be("PING");

		await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false);

		var router = await routerSource.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
		var closed = await router.Closed.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
		closed.Reason.Should().Be(SessionCloseReason.ClientDisconnected);

		await gate.StopAsync().ConfigureAwait(false);
	}

	[Fact]
	public async Task ReconnectionCreatesNewSession()
	{
		await using var system = new ActorSystem();
		var echo = await system.CreateActorAsync(() => new EchoActor()).ConfigureAwait(false);
		var firstRouter = new TaskCompletionSource<TestSessionRouter>(TaskCreationOptions.RunContinuationsAsynchronously);
		var secondRouter = new TaskCompletionSource<TestSessionRouter>(TaskCreationOptions.RunContinuationsAsynchronously);
		var routerQueue = new Queue<TaskCompletionSource<TestSessionRouter>>(new[] { firstRouter, secondRouter });
		var options = new GateServerOptions
		{
			TcpPort = 0,
			EnableWebSockets = false,
			RouterFactory = context =>
			{
				var router = new TestSessionRouter(echo.Handle);
				if (routerQueue.Count > 0)
				{
					routerQueue.Dequeue().TrySetResult(router);
				}
				return router;
			}
		};

		await using var gate = new GateServer(system, options, NullLogger<GateServer>.Instance);
		await gate.StartAsync().ConfigureAwait(false);
		var endpoint = gate.TcpEndpoint!;

		await using (var client = new TcpClient())
		{
			await client.ConnectAsync(endpoint.Address, endpoint.Port).ConfigureAwait(false);
			await using var stream = client.GetStream();
			await WriteFrameAsync(stream, "one").ConfigureAwait(false);
			await ReadFrameAsync(stream).ConfigureAwait(false);
		}

		var first = await firstRouter.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
		await first.Closed.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

		await using var secondClient = new TcpClient();
		await secondClient.ConnectAsync(endpoint.Address, endpoint.Port).ConfigureAwait(false);
		await using var secondStream = secondClient.GetStream();
		await WriteFrameAsync(secondStream, "two").ConfigureAwait(false);
		var response = await ReadFrameAsync(secondStream).ConfigureAwait(false);
		response.Should().Be("TWO");

		var second = await secondRouter.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
		second.Metadata.SessionId.Should().NotBe(first.Metadata.SessionId);

		await gate.StopAsync().ConfigureAwait(false);
	}

	private static async Task WriteFrameAsync(NetworkStream stream, string payload)
	{
		var bytes = Encoding.UTF8.GetBytes(payload);
		var length = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(bytes.Length));
		await stream.WriteAsync(length, 0, length.Length).ConfigureAwait(false);
		await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
	}

	private static async Task<string> ReadFrameAsync(NetworkStream stream)
	{
		var header = new byte[4];
		await stream.ReadExactlyAsync(header, 0, header.Length).ConfigureAwait(false);
		var length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header, 0));
		var buffer = new byte[length];
		await stream.ReadExactlyAsync(buffer, 0, length).ConfigureAwait(false);
		return Encoding.UTF8.GetString(buffer);
	}

	private static int GetFreeTcpPort()
	{
		var listener = new TcpListener(IPAddress.Loopback, 0);
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
				EchoRequest request => Task.FromResult<object?>(request.Text.ToUpperInvariant()),
				_ => throw new InvalidOperationException("Unexpected payload")
			};
		}
	}

	private sealed record EchoRequest(string Text);

	private sealed class TestSessionRouter : ISessionMessageRouter
	{
		private readonly ActorHandle _echo;
		private SessionContext? _context;
		private readonly TaskCompletionSource<(SessionCloseReason Reason, string? Description)> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public TestSessionRouter(ActorHandle echo)
		{
			_echo = echo;
		}

		public SessionMetadata Metadata => _context!.Metadata;

		public Task<(SessionCloseReason Reason, string? Description)> Closed => _closed.Task;

		public Task OnSessionStartedAsync(SessionContext context, CancellationToken cancellationToken)
		{
			_context = context;
			return Task.CompletedTask;
		}

		public async Task OnSessionMessageAsync(SessionContext context, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
		{
			var text = Encoding.UTF8.GetString(payload.Span);
			var reply = await context.CallAsync<string>(_echo, new EchoRequest(text), cancellationToken: cancellationToken).ConfigureAwait(false);
			await context.SendAsync(Encoding.UTF8.GetBytes(reply), cancellationToken).ConfigureAwait(false);
		}

		public Task OnSessionClosedAsync(SessionContext context, SessionCloseReason reason, string? description, CancellationToken cancellationToken)
		{
			_closed.TrySetResult((reason, description));
			return Task.CompletedTask;
		}
	}
}

internal static class NetworkStreamExtensions
{
	public static async Task ReadExactlyAsync(this NetworkStream stream, byte[] buffer, int offset, int count)
	{
		var read = 0;
		while (read < count)
		{
			var bytes = await stream.ReadAsync(buffer, offset + read, count - read).ConfigureAwait(false);
			if (bytes == 0)
			{
				throw new IOException("Stream closed before expected bytes were received.");
			}
			read += bytes;
		}
	}
}

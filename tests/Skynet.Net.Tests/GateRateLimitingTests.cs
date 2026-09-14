using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Skynet.Core;
using Xunit;

namespace Skynet.Net.Tests;

/// <summary>
/// Integration tests wiring <see cref="IGateRateLimiter"/> into the <see cref="GateServer"/> session
/// pipeline (TCP and WebSocket share the same path, TCP is exercised here).
/// </summary>
public sealed class GateRateLimitingTests
{
	private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

	[Fact]
	public async Task DropDecision_ShouldSkipFrameAndKeepConnectionAlive()
	{
		await using var system = new ActorSystem();
		var echo = await system.CreateActorAsync(() => new RateLimitEchoActor()).ConfigureAwait(false);
		var routerSource = new TaskCompletionSource<CountingEchoRouter>(TaskCreationOptions.RunContinuationsAsynchronously);
		// Drop the second frame and allow everything else, deterministically.
		var limiter = new CountingRateLimiter(dropEvery: 2);
		var options = new GateServerOptions
		{
			TcpPort = 0,
			EnableWebSockets = false,
			RateLimiter = limiter,
			RouterFactory = _ =>
			{
				var router = new CountingEchoRouter(echo.Handle);
				routerSource.TrySetResult(router);
				return router;
			}
		};

		await using var gate = new GateServer(system, options, NullLogger<GateServer>.Instance);
		await gate.StartAsync().ConfigureAwait(false);
		var endpoint = gate.TcpEndpoint!;

		using var client = new TcpClient();
		await client.ConnectAsync(endpoint.Address, endpoint.Port).WaitAsync(WaitTimeout).ConfigureAwait(false);
		var stream = client.GetStream();

		await WriteFrameAsync(stream, "A").ConfigureAwait(false);
		await WriteFrameAsync(stream, "B").ConfigureAwait(false);
		await WriteFrameAsync(stream, "C").ConfigureAwait(false);

		var replies = new List<string>
		{
			await ReadFrameAsync(stream).ConfigureAwait(false),
			await ReadFrameAsync(stream).ConfigureAwait(false)
		};
		replies.Should().Equal("A", "C");

		var router = await routerSource.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
		router.MessageCount.Should().Be(2);

		await gate.StopAsync().ConfigureAwait(false);
	}

	[Fact]
	public async Task CloseDecision_ShouldTerminateSessionWithProtocolViolation()
	{
		await using var system = new ActorSystem();
		var echo = await system.CreateActorAsync(() => new RateLimitEchoActor()).ConfigureAwait(false);
		var routerSource = new TaskCompletionSource<CountingEchoRouter>(TaskCreationOptions.RunContinuationsAsynchronously);
		var options = new GateServerOptions
		{
			TcpPort = 0,
			EnableWebSockets = false,
			RateLimiter = new CountingRateLimiter(closeAfter: 1),
			RouterFactory = _ =>
			{
				var router = new CountingEchoRouter(echo.Handle);
				routerSource.TrySetResult(router);
				return router;
			}
		};

		await using var gate = new GateServer(system, options, NullLogger<GateServer>.Instance);
		await gate.StartAsync().ConfigureAwait(false);
		var endpoint = gate.TcpEndpoint!;

		using var client = new TcpClient();
		await client.ConnectAsync(endpoint.Address, endpoint.Port).WaitAsync(WaitTimeout).ConfigureAwait(false);
		var stream = client.GetStream();

		await WriteFrameAsync(stream, "A").ConfigureAwait(false);
		var router = await routerSource.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
		var closed = await router.Closed.WaitAsync(WaitTimeout).ConfigureAwait(false);
		closed.Reason.Should().Be(SessionCloseReason.ProtocolViolation);
		closed.Description.Should().Be("Inbound frame rate limit exceeded.");

		var eof = await ReadFrameOrNullAsync(stream).ConfigureAwait(false);
		eof.Should().BeNull();

		await gate.StopAsync().ConfigureAwait(false);
	}

	[Fact]
	public async Task ConnectionAdmissionClose_ShouldRejectBeforeSessionCreation()
	{
		await using var system = new ActorSystem();
		var echo = await system.CreateActorAsync(() => new RateLimitEchoActor()).ConfigureAwait(false);
		var routerCreated = new TaskCompletionSource<CountingEchoRouter>(TaskCreationOptions.RunContinuationsAsynchronously);
		var options = new GateServerOptions
		{
			TcpPort = 0,
			EnableWebSockets = false,
			RateLimiter = new CountingRateLimiter(rejectConnections: true),
			RouterFactory = _ =>
			{
				var router = new CountingEchoRouter(echo.Handle);
				routerCreated.TrySetResult(router);
				return router;
			}
		};

		await using var gate = new GateServer(system, options, NullLogger<GateServer>.Instance);
		await gate.StartAsync().ConfigureAwait(false);
		var endpoint = gate.TcpEndpoint!;

		using var client = new TcpClient();
		await client.ConnectAsync(endpoint.Address, endpoint.Port).WaitAsync(WaitTimeout).ConfigureAwait(false);

		var eof = await ReadFrameOrNullAsync(client.GetStream()).ConfigureAwait(false);
		eof.Should().BeNull();

		// Give the accept pipeline a moment to prove no session actor is ever created.
		await Task.Delay(300).ConfigureAwait(false);
		routerCreated.Task.IsCompleted.Should().BeFalse();

		await gate.StopAsync().ConfigureAwait(false);
	}

	[Fact]
	public async Task TokenBucketLimiter_ShouldThrottleThroughGate()
	{
		await using var system = new ActorSystem();
		var echo = await system.CreateActorAsync(() => new RateLimitEchoActor()).ConfigureAwait(false);
		var routerSource = new TaskCompletionSource<CountingEchoRouter>(TaskCreationOptions.RunContinuationsAsynchronously);
		var options = new GateServerOptions
		{
			TcpPort = 0,
			EnableWebSockets = false,
			// Two-token burst, one token every 500 ms: the third rapid frame is deterministically dropped.
			RateLimiter = new TokenBucketGateRateLimiter(maxConnectionsPerIp: 8, inboundFramesPerSecond: 2, inboundFrameBurst: 2),
			RouterFactory = _ =>
			{
				var router = new CountingEchoRouter(echo.Handle);
				routerSource.TrySetResult(router);
				return router;
			}
		};

		await using var gate = new GateServer(system, options, NullLogger<GateServer>.Instance);
		await gate.StartAsync().ConfigureAwait(false);
		var endpoint = gate.TcpEndpoint!;

		using var client = new TcpClient();
		await client.ConnectAsync(endpoint.Address, endpoint.Port).WaitAsync(WaitTimeout).ConfigureAwait(false);
		var stream = client.GetStream();

		await WriteFrameAsync(stream, "A").ConfigureAwait(false);
		await WriteFrameAsync(stream, "B").ConfigureAwait(false);
		await WriteFrameAsync(stream, "C").ConfigureAwait(false);

		var first = await ReadFrameAsync(stream).ConfigureAwait(false);
		var second = await ReadFrameAsync(stream).ConfigureAwait(false);
		(first, second).Should().Be(("A", "B"));

		// The gate processes frames serially ahead of the replies, so "C" has already been dropped
		// once "A" and "B" came back: exactly two frames may have reached the router.
		var router = await routerSource.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
		await Task.Delay(100).ConfigureAwait(false);
		router.MessageCount.Should().Be(2);

		// The connection stays usable and the bucket refills (2 fps: a token is ready within ~500 ms).
		await Task.Delay(700).ConfigureAwait(false);
		await WriteFrameAsync(stream, "D").ConfigureAwait(false);
		var late = await ReadFrameAsync(stream).ConfigureAwait(false);
		late.Should().Be("D");

		await gate.StopAsync().ConfigureAwait(false);
	}

	private static async Task WriteFrameAsync(NetworkStream stream, string payload)
	{
		var bytes = Encoding.UTF8.GetBytes(payload);
		var length = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(bytes.Length));
		await stream.WriteAsync(length, 0, length.Length).WaitAsync(WaitTimeout).ConfigureAwait(false);
		await stream.WriteAsync(bytes, 0, bytes.Length).WaitAsync(WaitTimeout).ConfigureAwait(false);
	}

	private static async Task<string> ReadFrameAsync(NetworkStream stream)
	{
		var header = new byte[4];
		await ReadExactlyAsync(stream, header).ConfigureAwait(false);
		var length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header, 0));
		var buffer = new byte[length];
		await ReadExactlyAsync(stream, buffer).ConfigureAwait(false);
		return Encoding.UTF8.GetString(buffer);
	}

	private static async Task<string?> ReadFrameOrNullAsync(NetworkStream stream)
	{
		try
		{
			return await ReadFrameAsync(stream).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
		{
			return null;
		}
	}

	private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer)
	{
		var read = 0;
		while (read < buffer.Length)
		{
			var bytes = await stream.ReadAsync(buffer, read, buffer.Length - read).WaitAsync(WaitTimeout).ConfigureAwait(false);
			if (bytes == 0)
			{
				throw new IOException("Stream closed before expected bytes were received.");
			}

			read += bytes;
		}
	}

	/// <summary>Deterministic stub: optionally rejects all connections, drops every Nth frame or closes after N frames.</summary>
	private sealed class CountingRateLimiter : IGateRateLimiter
	{
		private readonly bool _rejectConnections;
		private readonly int _dropEvery;
		private readonly int _closeAfter;
		private int _frames;

		public CountingRateLimiter(bool rejectConnections = false, int dropEvery = 0, int closeAfter = 0)
		{
			_rejectConnections = rejectConnections;
			_dropEvery = dropEvery;
			_closeAfter = closeAfter;
		}

		public GateRateLimitDecision EvaluateConnection(SessionMetadata metadata)
		{
			return _rejectConnections ? GateRateLimitDecision.Close : GateRateLimitDecision.Allow;
		}

		public GateRateLimitDecision EvaluateInboundFrame(SessionMetadata metadata, int payloadSize)
		{
			var index = Interlocked.Increment(ref _frames);
			if (_closeAfter > 0 && index >= _closeAfter)
			{
				return GateRateLimitDecision.Close;
			}

			if (_dropEvery > 0 && index % _dropEvery == 0)
			{
				return GateRateLimitDecision.Drop;
			}

			return GateRateLimitDecision.Allow;
		}

		public void OnSessionClosed(string sessionId)
		{
		}
	}

	private sealed class RateLimitEchoActor : Actor
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

	private sealed class CountingEchoRouter : ISessionMessageRouter
	{
		private readonly ActorHandle _echo;
		private SessionContext? _context;
		private int _messageCount;
		private readonly TaskCompletionSource<(SessionCloseReason Reason, string? Description)> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public CountingEchoRouter(ActorHandle echo)
		{
			_echo = echo;
		}

		public int MessageCount => Volatile.Read(ref _messageCount);

		public Task<(SessionCloseReason Reason, string? Description)> Closed => _closed.Task;

		public Task OnSessionStartedAsync(SessionContext context, CancellationToken cancellationToken)
		{
			_context = context;
			return Task.CompletedTask;
		}

		public async Task OnSessionMessageAsync(SessionContext context, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref _messageCount);
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

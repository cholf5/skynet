using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Skynet.Core;
using Xunit;

namespace Skynet.Net.Tests;

/// <summary>
/// Unit tests for the <see cref="MsgServerRouter"/> command dispatch and Gate integration tests covering
/// the full client → gate → MsgServer → actor → client path over real TCP sockets.
/// </summary>
public sealed class MsgServerRouterTests
{
	private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

	// --- Unit tests (dispatch is exercised directly against a SessionContext backed by a fake connection) ---

	[Fact]
	public async Task RegisteredCommand_DispatchesBusinessPayloadWithoutCommandByte()
	{
		await using var system = new ActorSystem();
		var connection = new FakeSessionConnection();
		var context = NewContext(system, connection);
		var router = new MsgServerRouter();
		router.IsRegistered(0x01).Should().BeFalse();

		byte[]? received = null;
		SessionContext? receivedContext = null;
		router.RegisterCommand(0x01, (ctx, payload, _) =>
		{
			received = payload.ToArray();
			receivedContext = ctx;
			return ValueTask.CompletedTask;
		});
		router.IsRegistered(0x01).Should().BeTrue();

		await router.OnSessionMessageAsync(context, new byte[] { 0x01, 0x0A, 0x0B }, CancellationToken.None)
			.WaitAsync(WaitTimeout);

		received.Should().Equal(0x0A, 0x0B);
		receivedContext.Should().BeSameAs(context);
	}

	[Fact]
	public async Task UnknownCommand_ReceivesErrorFrame()
	{
		await using var system = new ActorSystem();
		var connection = new FakeSessionConnection();
		var context = NewContext(system, connection);
		var router = new MsgServerRouter();

		await router.OnSessionMessageAsync(context, new byte[] { 0x7F, 0x01 }, CancellationToken.None)
			.WaitAsync(WaitTimeout);

		AssertErrorFrame(await connection.ExpectFrameAsync(), 0x7F, "unknown command 0x7F");
	}

	[Fact]
	public async Task EmptyFrame_ReceivesErrorFrame()
	{
		await using var system = new ActorSystem();
		var connection = new FakeSessionConnection();
		var context = NewContext(system, connection);
		var router = new MsgServerRouter();

		await router.OnSessionMessageAsync(context, Array.Empty<byte>(), CancellationToken.None)
			.WaitAsync(WaitTimeout);

		AssertErrorFrame(await connection.ExpectFrameAsync(), MsgServerRouter.ErrorFrameMarker, "empty frame");
	}

	[Fact]
	public async Task HandlerFailure_SendsErrorFrameAndKeepsDispatching()
	{
		await using var system = new ActorSystem();
		var connection = new FakeSessionConnection();
		var context = NewContext(system, connection);
		var router = new MsgServerRouter();
		router.RegisterCommand(0x02, (_, _, _) => throw new InvalidOperationException("boom"));

		await router.OnSessionMessageAsync(context, new byte[] { 0x02, 0x01 }, CancellationToken.None)
			.WaitAsync(WaitTimeout);

		AssertErrorFrame(await connection.ExpectFrameAsync(), 0x02, "handler error: InvalidOperationException: boom");

		// The session stays alive: frames registered after the failure are still dispatched.
		byte[]? received = null;
		router.RegisterCommand(0x03, (_, payload, _) =>
		{
			received = payload.ToArray();
			return ValueTask.CompletedTask;
		});
		await router.OnSessionMessageAsync(context, new byte[] { 0x03, 0x09 }, CancellationToken.None)
			.WaitAsync(WaitTimeout);
		received.Should().Equal(0x09);
	}

	[Fact]
	public async Task TargetActorFailure_SendsErrorFrame()
	{
		await using var system = new ActorSystem();
		var throwing = await system.CreateActorAsync(() => new ThrowingActor()).WaitAsync(WaitTimeout);
		var connection = new FakeSessionConnection();
		var context = NewContext(system, connection);
		var router = new MsgServerRouter();
		router.ForwardCommand(0x02, throwing.Handle);

		await router.OnSessionMessageAsync(context, new byte[] { 0x02, 0x01 }, CancellationToken.None)
			.WaitAsync(WaitTimeout);

		AssertErrorFrame(await connection.ExpectFrameAsync(), 0x02, "handler error: InvalidOperationException: actor exploded");
	}

	[Fact]
	public async Task ForwardCommand_RoundTripsByteResponse()
	{
		await using var system = new ActorSystem();
		var echo = await system.CreateActorAsync(() => new ByteEchoActor()).WaitAsync(WaitTimeout);
		var connection = new FakeSessionConnection();
		var context = NewContext(system, connection);
		var router = new MsgServerRouter();
		router.ForwardCommand(0x02, echo.Handle);

		await router.OnSessionMessageAsync(context, new byte[] { 0x02, (byte)'a', (byte)'b' }, CancellationToken.None)
			.WaitAsync(WaitTimeout);

		(await connection.ExpectFrameAsync()).Should().Equal((byte)'A', (byte)'B');
	}

	[Fact]
	public async Task ForwardCommand_UnsupportedOrNullResponse_SendsErrorFrame()
	{
		await using var system = new ActorSystem();
		var intActor = await system.CreateActorAsync(() => new IntActor()).WaitAsync(WaitTimeout);
		var nullActor = await system.CreateActorAsync(() => new NullActor()).WaitAsync(WaitTimeout);
		var connection = new FakeSessionConnection();
		var context = NewContext(system, connection);
		var router = new MsgServerRouter();
		router.ForwardCommand(0x04, intActor.Handle);
		router.ForwardCommand(0x05, nullActor.Handle);

		await router.OnSessionMessageAsync(context, new byte[] { 0x04 }, CancellationToken.None)
			.WaitAsync(WaitTimeout);
		AssertErrorFrame(
			await connection.ExpectFrameAsync(),
			0x04,
			"handler error: InvalidOperationException: The target actor returned unsupported response type System.Int32; expected byte[], ReadOnlyMemory<byte> or string.");

		await router.OnSessionMessageAsync(context, new byte[] { 0x05 }, CancellationToken.None)
			.WaitAsync(WaitTimeout);
		AssertErrorFrame(
			await connection.ExpectFrameAsync(),
			0x05,
			"handler error: InvalidOperationException: The target actor returned a null response.");
	}

	[Fact]
	public void RegisterCommand_DuplicateCommand_ThrowsArgumentException()
	{
		var router = new MsgServerRouter();
		router.RegisterCommand(0x01, NoopHandler);

		Action act = () => router.RegisterCommand(0x01, NoopHandler);

		act.Should().Throw<ArgumentException>();
	}

	[Fact]
	public void RegisterCommand_ReservedCommandByte_ThrowsArgumentException()
	{
		var router = new MsgServerRouter();

		Action register = () => router.RegisterCommand(MsgServerRouter.ErrorFrameMarker, NoopHandler);
		Action forward = () => router.ForwardCommand(MsgServerRouter.ErrorFrameMarker, new ActorHandle(1));

		register.Should().Throw<ArgumentException>();
		forward.Should().Throw<ArgumentException>();
	}

	[Fact]
	public void ForwardCommand_InvalidHandle_ThrowsArgumentException()
	{
		var router = new MsgServerRouter();

		Action act = () => router.ForwardCommand(0x01, ActorHandle.None);

		act.Should().Throw<ArgumentException>();
	}

	[Fact]
	public async Task RegisterCommand_AfterSessionStarted_ThrowsInvalidOperationException()
	{
		await using var system = new ActorSystem();
		var router = new MsgServerRouter();
		await router.OnSessionStartedAsync(NewContext(system, new FakeSessionConnection()), CancellationToken.None);

		Action act = () => router.RegisterCommand(0x01, NoopHandler);

		act.Should().Throw<InvalidOperationException>();
	}

	[Fact]
	public void EncodeErrorFrame_TruncatesLongErrorText()
	{
		var frame = MsgServerRouter.EncodeErrorFrame(0x09, new string('x', 1000));

		frame.Length.Should().Be(MsgServerRouter.MaxErrorTextBytes + 2);
		frame[0].Should().Be(MsgServerRouter.ErrorFrameMarker);
		frame[1].Should().Be(0x09);
		Encoding.UTF8.GetString(frame[2..]).Should().Be(new string('x', MsgServerRouter.MaxErrorTextBytes));
	}

	// --- Gate integration tests (real TCP sockets, dynamic port, client framed wire traffic) ---

	[Fact]
	public async Task Gate_RegisteredCommand_RoundTripsThroughEchoActor()
	{
		await using var system = new ActorSystem();
		var echo = await system.CreateActorAsync(() => new EchoActor()).WaitAsync(WaitTimeout);
		var byteEcho = await system.CreateActorAsync(() => new ByteEchoActor()).WaitAsync(WaitTimeout);
		var options = new GateServerOptions
		{
			TcpPort = 0,
			EnableWebSockets = false,
			RouterFactory = _ => NewRouter(echo.Handle, byteEcho.Handle)
		};

		await using var gate = new GateServer(system, options, NullLogger<GateServer>.Instance);
		await gate.StartAsync().WaitAsync(WaitTimeout);
		var endpoint = gate.TcpEndpoint!;
		endpoint.Should().NotBeNull();

		using var client = new TcpClient();
		await client.ConnectAsync(endpoint.Address, endpoint.Port).WaitAsync(WaitTimeout);
		var stream = client.GetStream();

		// Command 0x01: custom handler decodes the payload, calls the echo actor and writes the reply.
		await WriteFrameAsync(stream, new byte[] { 0x01, (byte)'h', (byte)'i' }).ConfigureAwait(false);
		(await ReadFrameAsync(stream)).Should().Equal((byte)'H', (byte)'I');

		// Command 0x02: built-in forwarding handler talks to the byte echo actor directly.
		await WriteFrameAsync(stream, new byte[] { 0x02, (byte)'a', (byte)'b', (byte)'c' }).ConfigureAwait(false);
		(await ReadFrameAsync(stream)).Should().Equal((byte)'A', (byte)'B', (byte)'C');

		await gate.StopAsync().ConfigureAwait(false);
	}

	[Fact]
	public async Task Gate_UnknownCommand_SendsErrorFrameAndKeepsSessionAlive()
	{
		await using var system = new ActorSystem();
		var echo = await system.CreateActorAsync(() => new EchoActor()).WaitAsync(WaitTimeout);
		var byteEcho = await system.CreateActorAsync(() => new ByteEchoActor()).WaitAsync(WaitTimeout);
		var options = new GateServerOptions
		{
			TcpPort = 0,
			EnableWebSockets = false,
			RouterFactory = _ => NewRouter(echo.Handle, byteEcho.Handle)
		};

		await using var gate = new GateServer(system, options, NullLogger<GateServer>.Instance);
		await gate.StartAsync().WaitAsync(WaitTimeout);
		var endpoint = gate.TcpEndpoint!;

		using var client = new TcpClient();
		await client.ConnectAsync(endpoint.Address, endpoint.Port).WaitAsync(WaitTimeout);
		var stream = client.GetStream();

		await WriteFrameAsync(stream, new byte[] { 0x7F, 0xAA }).ConfigureAwait(false);
		AssertErrorFrame(await ReadFrameAsync(stream), 0x7F, "unknown command 0x7F");

		// The connection is still usable: a valid command after the error gets a regular response.
		await WriteFrameAsync(stream, new byte[] { 0x01, (byte)'o', (byte)'k' }).ConfigureAwait(false);
		(await ReadFrameAsync(stream)).Should().Equal((byte)'O', (byte)'K');

		await gate.StopAsync().ConfigureAwait(false);
	}

	[Fact]
	public async Task Gate_HandlerFailure_SendsErrorFrameAndKeepsSessionAlive()
	{
		await using var system = new ActorSystem();
		var echo = await system.CreateActorAsync(() => new EchoActor()).WaitAsync(WaitTimeout);
		var byteEcho = await system.CreateActorAsync(() => new ByteEchoActor()).WaitAsync(WaitTimeout);
		var options = new GateServerOptions
		{
			TcpPort = 0,
			EnableWebSockets = false,
			RouterFactory = _ => NewRouter(echo.Handle, byteEcho.Handle)
		};

		await using var gate = new GateServer(system, options, NullLogger<GateServer>.Instance);
		await gate.StartAsync().WaitAsync(WaitTimeout);
		var endpoint = gate.TcpEndpoint!;

		using var client = new TcpClient();
		await client.ConnectAsync(endpoint.Address, endpoint.Port).WaitAsync(WaitTimeout);
		var stream = client.GetStream();

		await WriteFrameAsync(stream, new byte[] { 0x03, 0x01 }).ConfigureAwait(false);
		AssertErrorFrame(await ReadFrameAsync(stream), 0x03, "handler error: InvalidOperationException: broken handler");

		// The session survives the failed handler: subsequent commands are still processed.
		await WriteFrameAsync(stream, new byte[] { 0x01, (byte)'o', (byte)'k' }).ConfigureAwait(false);
		(await ReadFrameAsync(stream)).Should().Equal((byte)'O', (byte)'K');

		await gate.StopAsync().ConfigureAwait(false);
	}

	private static MsgServerRouter NewRouter(ActorHandle echo, ActorHandle byteEcho)
	{
		var router = new MsgServerRouter();
		router.RegisterCommand(0x01, async (context, payload, cancellationToken) =>
		{
			var request = new EchoRequest(Encoding.UTF8.GetString(payload.Span));
			var reply = await context.CallAsync<string>(echo, request, cancellationToken: cancellationToken)
				.ConfigureAwait(false);
			await context.SendAsync(Encoding.UTF8.GetBytes(reply), cancellationToken).ConfigureAwait(false);
		});
		router.ForwardCommand(0x02, byteEcho);
		router.RegisterCommand(0x03, (_, _, _) => throw new InvalidOperationException("broken handler"));
		return router;
	}

	private static SessionContext NewContext(ActorSystem system, ISessionConnection connection)
	{
		return new SessionContext(
			system,
			ActorHandle.None,
			connection,
			new SessionMetadata("test-session", "test", null, DateTimeOffset.UtcNow),
			NullLogger.Instance);
	}

	private static MsgCommandHandler NoopHandler => (_, _, _) => ValueTask.CompletedTask;

	private static void AssertErrorFrame(byte[] frame, byte command, string expectedText)
	{
		frame.Length.Should().BeGreaterThanOrEqualTo(2);
		frame[0].Should().Be(MsgServerRouter.ErrorFrameMarker);
		frame[1].Should().Be(command);
		Encoding.UTF8.GetString(frame[2..]).Should().Be(expectedText);
	}

	private static async Task WriteFrameAsync(NetworkStream stream, byte[] payload)
	{
		var header = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payload.Length));
		await stream.WriteAsync(header).AsTask().WaitAsync(WaitTimeout).ConfigureAwait(false);
		await stream.WriteAsync(payload).AsTask().WaitAsync(WaitTimeout).ConfigureAwait(false);
	}

	private static async Task<byte[]> ReadFrameAsync(NetworkStream stream)
	{
		var header = new byte[4];
		await ReadExactlyAsync(stream, header).ConfigureAwait(false);
		var length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header, 0));
		var payload = new byte[length];
		await ReadExactlyAsync(stream, payload).ConfigureAwait(false);
		return payload;
	}

	private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer)
	{
		var read = 0;
		while (read < buffer.Length)
		{
			var bytes = await stream.ReadAsync(buffer.AsMemory(read)).AsTask().WaitAsync(WaitTimeout).ConfigureAwait(false);
			if (bytes == 0)
			{
				throw new IOException("Stream closed before expected bytes were received.");
			}

			read += bytes;
		}
	}

	private sealed class FakeSessionConnection : ISessionConnection
	{
		private readonly Channel<byte[]> _frames = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
		{
			SingleReader = true,
			SingleWriter = false
		});

		public EndPoint? RemoteEndPoint => null;

		public DateTimeOffset LastActivity { get; private set; } = DateTimeOffset.UtcNow;

		public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
		{
			LastActivity = DateTimeOffset.UtcNow;
			return new ValueTask(_frames.Writer.WriteAsync(payload.ToArray(), cancellationToken).AsTask());
		}

		public ValueTask CloseAsync(SessionCloseReason reason, string? description, CancellationToken cancellationToken)
		{
			LastActivity = DateTimeOffset.UtcNow;
			_frames.Writer.TryComplete();
			return ValueTask.CompletedTask;
		}

		public void MarkActivity()
		{
			LastActivity = DateTimeOffset.UtcNow;
		}

		public ValueTask DisposeAsync()
		{
			_frames.Writer.TryComplete();
			return ValueTask.CompletedTask;
		}

		public async Task<byte[]> ExpectFrameAsync()
		{
			return await _frames.Reader.ReadAsync(CancellationToken.None).AsTask()
				.WaitAsync(WaitTimeout).ConfigureAwait(false);
		}
	}

	private sealed record EchoRequest(string Text);

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

	/// <summary>Receives a raw byte[] payload and answers with an uppercased byte[] copy.</summary>
	private sealed class ByteEchoActor : Actor
	{
		protected override Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
			return envelope.Payload switch
			{
				byte[] bytes => Task.FromResult<object?>(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).ToUpperInvariant())),
				_ => throw new InvalidOperationException("Unexpected payload")
			};
		}
	}

	private sealed class ThrowingActor : Actor
	{
		protected override Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
			throw new InvalidOperationException("actor exploded");
		}
	}

	private sealed class IntActor : Actor
	{
		protected override Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
			return Task.FromResult<object?>(42);
		}
	}

	private sealed class NullActor : Actor
	{
		protected override Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
			return Task.FromResult<object?>(null);
		}
	}
}

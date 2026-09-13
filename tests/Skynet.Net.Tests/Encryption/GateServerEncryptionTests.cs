using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Skynet.Core;
using Skynet.Net.Encryption;
using Xunit;

namespace Skynet.Net.Tests.Encryption;

/// <summary>
/// End-to-end handshake tests over a real TCP loopback connection against a running GateServer.
/// </summary>
public sealed class GateServerEncryptionTests
{
	private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

	[Fact]
	public async Task FullHandshakeRoundtripsEncryptedBusinessFrames()
	{
		await using var fixture = await GateFixture.StartAsync(options => options.EnableEncryption = true)
			.ConfigureAwait(false);
		var client = fixture.CreateClient();

		try
		{
			await client.ConnectAsync().ConfigureAwait(false);
			await client.HandshakeAsync().ConfigureAwait(false);
			client.Session.IsCompleted.Should().BeTrue();

			await client.SendBusinessAsync("hello").ConfigureAwait(false);
			var reply = await client.ReceiveBusinessAsync().ConfigureAwait(false);

			reply.Should().Be("HELLO");
			// The authentication callback captured the gate-side session key; both endpoints derived the
			// same key from the seed delivered over RSA.
			var serverKey = await fixture.SessionKeyTask.WaitAsync(WaitTimeout).ConfigureAwait(false);
			serverKey.Should().NotBeNull();
			serverKey.Should().Equal(client.Session.SessionKey);
		}
		finally
		{
			await client.DisposeAsync().ConfigureAwait(false);
		}
	}

	[Fact]
	public async Task BusinessFrameBeforeHandshakeIsRejectedWithErrorCode()
	{
		await using var fixture = await GateFixture.StartAsync(options => options.EnableEncryption = true)
			.ConfigureAwait(false);
		var client = fixture.CreateClient();

		try
		{
			await client.ConnectAsync().ConfigureAwait(false);
			await client.WriteRawAsync(Encoding.UTF8.GetBytes("hello")).ConfigureAwait(false);

			var raw = await client.ReadRawOrNullAsync().ConfigureAwait(false);
			raw.Should().NotBeNull();
			GateFrameCodec.TryDecodeErrorFrame(raw!).Should().Be(GateHandshakeErrors.BusinessFrameBeforeHandshake);

			// The gate closes the connection after the diagnostic error frame.
			var eof = await client.ReadRawOrNullAsync().ConfigureAwait(false);
			eof.Should().BeNull();
		}
		finally
		{
			await client.DisposeAsync().ConfigureAwait(false);
		}
	}

	[Fact]
	public async Task AuthCallbackRejectionClosesConnectionBeforeSessionActorIsCreated()
	{
		var routerCreated = false;
		await using var fixture = await GateFixture.StartAsync(options =>
		{
			options.EnableEncryption = true;
			options.AuthCallback = (_, _) => ValueTask.FromResult(false);
		}, onRouterCreated: () => routerCreated = true).ConfigureAwait(false);
		var client = fixture.CreateClient();

		try
		{
			await client.ConnectAsync().ConfigureAwait(false);

			// The gate answers the confirm with a HandshakeError frame and closes the connection.
			Func<Task> act = () => client.HandshakeAsync();
			(await act.Should().ThrowAsync<GateHandshakeException>()).Which.Code.Should().Be(GateHandshakeErrors.AuthRejected);

			routerCreated.Should().BeFalse();
		}
		finally
		{
			await client.DisposeAsync().ConfigureAwait(false);
		}
	}

	[Fact]
	public async Task HandshakeTimeoutClosesIdleConnection()
	{
		await using var fixture = await GateFixture.StartAsync(options =>
		{
			options.EnableEncryption = true;
			options.HandshakeTimeout = TimeSpan.FromMilliseconds(500);
		}).ConfigureAwait(false);
		var client = fixture.CreateClient();

		try
		{
			await client.ConnectAsync().ConfigureAwait(false);

			// The client never starts the handshake; the gate must close the connection.
			var eof = await client.ReadRawOrNullAsync().ConfigureAwait(false);
			eof.Should().BeNull();
		}
		finally
		{
			await client.DisposeAsync().ConfigureAwait(false);
		}
	}

	[Fact]
	public async Task EncryptionDisabledKeepsLegacyPlaintextPath()
	{
		await using var fixture = await GateFixture.StartAsync(options => options.EnableEncryption = false)
			.ConfigureAwait(false);
		var client = fixture.CreateClient();

		try
		{
			await client.ConnectAsync().ConfigureAwait(false);
			await client.WriteRawAsync(Encoding.UTF8.GetBytes("plain")).ConfigureAwait(false);
			var raw = await client.ReadRawOrNullAsync().ConfigureAwait(false);

			Encoding.UTF8.GetString(raw!).Should().Be("PLAIN");
		}
		finally
		{
			await client.DisposeAsync().ConfigureAwait(false);
		}
	}

	private sealed class GateFixture : IAsyncDisposable
	{
		private readonly ActorSystem _system;
		private readonly GateServer _gate;

		private GateFixture(ActorSystem system, GateServer gate)
		{
			_system = system;
			_gate = gate;
		}

		public IPEndPoint TcpEndpoint => _gate.TcpEndpoint!;

		public InMemoryGateRsaKeyProvider? KeyProvider { get; private set; }

		/// <summary>Completes with the session key captured by the gate authentication callback (encrypted mode only).</summary>
		public Task<byte[]> SessionKeyTask { get; private set; } = Task.FromResult<byte[]>([]);

		public static async Task<GateFixture> StartAsync(Action<GateServerOptions> configure, Action? onRouterCreated = null)
		{
			var system = new ActorSystem();
			var echo = await system.CreateActorAsync(() => new EchoActor()).ConfigureAwait(false);
			var gateOptions = new GateServerOptions
			{
				TcpPort = 0,
				EnableWebSockets = false,
				RouterFactory = context =>
				{
					onRouterCreated?.Invoke();
					return new TestEchoRouter(echo.Handle);
				}
			};
			configure(gateOptions);
			if (gateOptions.EnableEncryption && gateOptions.RsaKeyProvider is null)
			{
				// Share the keypair between gate and test client so the client can encrypt the confirm payload.
				gateOptions.RsaKeyProvider = InMemoryGateRsaKeyProvider.Generate(2048);
			}

			var sessionKeySource = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
			if (gateOptions.AuthCallback is null)
			{
				gateOptions.AuthCallback = (context, _) =>
				{
					sessionKeySource.TrySetResult(context.SessionKey?.ToArray() ?? []);
					return ValueTask.FromResult(true);
				};
			}

			var gate = new GateServer(system, gateOptions, NullLogger<GateServer>.Instance);
			await gate.StartAsync().ConfigureAwait(false);
			gate.TcpEndpoint.Should().NotBeNull();

			return new GateFixture(system, gate)
			{
				KeyProvider = (InMemoryGateRsaKeyProvider?)gateOptions.RsaKeyProvider,
				SessionKeyTask = sessionKeySource.Task,
			};
		}

		public GateTestClient CreateClient()
		{
			return new GateTestClient(TcpEndpoint, KeyProvider);
		}

		public async ValueTask DisposeAsync()
		{
			await _gate.StopAsync().ConfigureAwait(false);
			await _gate.DisposeAsync().ConfigureAwait(false);
			await _system.DisposeAsync().ConfigureAwait(false);
		}
	}
}

using System.IO;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Skynet.Core;
using Xunit;

namespace Skynet.Net.Tests;

/// <summary>
/// Reconnect behavior tests for the outbound Gate connection pool, ported from the reference
/// implementation's MicroKitGateReconnectTests (fake transport + fake delay strategy).
/// </summary>
public sealed class GateConnectionPoolTests
{
	private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

	[Fact]
	public async Task ManagerConnectsConfiguredGateEndpoints()
	{
		var transport = new FakeGateClientTransport();
		var manager = new GateClientManager(["127.0.0.1:10001", "127.0.0.1:10002"], transport);

		await manager.StartAsync();

		manager.Clients.Should().HaveCount(2);
		manager.Clients.Should().OnlyContain(client => client.State == GateClientState.Connected);
		transport.ConnectEndpoints.Select(endpoint => endpoint.Address)
			.Should().Equal("127.0.0.1:10001", "127.0.0.1:10002");

		await manager.StopAsync();
		manager.Clients.Should().OnlyContain(client => client.State == GateClientState.Stopped);
	}

	[Fact]
	public async Task StartDoesNotBlockOnUnreachableGateEndpoint()
	{
		var delayStrategy = new BlockingDelayStrategy();
		var transport = new FakeGateClientTransport { AlwaysFail = true };
		var manager = new GateClientManager(
			["gate-a"],
			transport,
			new ReconnectPolicy(maxAttempts: 3, initialDelay: TimeSpan.FromMinutes(1), maxDelay: TimeSpan.FromMinutes(1)),
			delayStrategy);

		await manager.StartAsync();
		await delayStrategy.WaitForDelayAsync();

		manager.Clients.Should().ContainSingle();
		manager.Clients[0].State.Should().Be(GateClientState.Disconnected);
		manager.Proxies[0].IsConnected.Should().BeFalse();

		await manager.StopAsync();
		delayStrategy.Cancelled.Should().BeTrue();
	}

	[Fact]
	public async Task RemoteDisconnectReconnectsAndReplacesConnection()
	{
		var transport = new FakeGateClientTransport();
		var client = new GateClient(
			new GateEndpoint("Gate0", "gate"),
			transport,
			new ReconnectPolicy(maxAttempts: 2, initialDelay: TimeSpan.Zero),
			NoopReconnectDelayStrategy.Instance);

		await client.StartAsync();
		var first = transport.Connections.Should().ContainSingle().Subject;
		var firstConnectionId = client.Proxy.ConnectionId;

		first.Disconnect(new InvalidOperationException("remote close"));
		await WaitUntilAsync(() => transport.ConnectAttempts >= 2);

		transport.ConnectAttempts.Should().Be(2);
		first.Disposed.Should().BeTrue();
		client.Proxy.IsConnected.Should().BeTrue();
		client.Proxy.ConnectionId.Should().NotBe(firstConnectionId);

		await client.StopAsync();
	}

	[Fact]
	public async Task RetryExhaustionLeavesClientDisconnected()
	{
		var transport = new FakeGateClientTransport { AlwaysFail = true };
		var client = new GateClient(
			new GateEndpoint("Gate0", "gate"),
			transport,
			new ReconnectPolicy(maxAttempts: 2, initialDelay: TimeSpan.Zero),
			NoopReconnectDelayStrategy.Instance);

		await client.StartAsync();
		await WaitUntilAsync(() => client.State == GateClientState.Disconnected && transport.ConnectAttempts == 3);

		client.State.Should().Be(GateClientState.Disconnected);
		client.Proxy.IsConnected.Should().BeFalse();
		transport.ConnectAttempts.Should().Be(3);
		client.LastError.Should().BeOfType<InvalidOperationException>();

		await client.StopAsync();
	}

	[Fact]
	public async Task StopCancelsPendingReconnectDelay()
	{
		var transport = new FakeGateClientTransport();
		var delayStrategy = new BlockingDelayStrategy();
		var client = new GateClient(
			new GateEndpoint("Gate0", "gate"),
			transport,
			new ReconnectPolicy(maxAttempts: 3, initialDelay: TimeSpan.FromMinutes(1), maxDelay: TimeSpan.FromMinutes(1)),
			delayStrategy);

		await client.StartAsync();
		var first = transport.Connections.Should().ContainSingle().Subject;
		transport.AlwaysFail = true;

		first.Disconnect();
		await delayStrategy.WaitForDelayAsync();

		await client.StopAsync();

		client.State.Should().Be(GateClientState.Stopped);
		client.Proxy.IsConnected.Should().BeFalse();
		delayStrategy.Cancelled.Should().BeTrue();
	}

	[Fact]
	public async Task StartWhileRetryingDoesNotCreateSecondConnectionLoop()
	{
		var transport = new FakeGateClientTransport { AlwaysFail = true };
		var delayStrategy = new BlockingDelayStrategy();
		var client = new GateClient(
			new GateEndpoint("Gate0", "gate"),
			transport,
			new ReconnectPolicy(maxAttempts: 3, initialDelay: TimeSpan.FromMinutes(1), maxDelay: TimeSpan.FromMinutes(1)),
			delayStrategy);

		await client.StartAsync();
		await delayStrategy.WaitForDelayAsync();
		var attemptsAfterFirstLoop = transport.ConnectAttempts;

		await client.StartAsync();

		transport.ConnectAttempts.Should().Be(attemptsAfterFirstLoop);
		await client.StopAsync();
	}

	[Fact]
	public async Task RestartAfterRetryExhaustionConnectsAgain()
	{
		var transport = new FakeGateClientTransport { AlwaysFail = true };
		var client = new GateClient(
			new GateEndpoint("Gate0", "gate"),
			transport,
			new ReconnectPolicy(maxAttempts: 1, initialDelay: TimeSpan.Zero),
			NoopReconnectDelayStrategy.Instance);

		await client.StartAsync();
		await WaitUntilAsync(() => client.State == GateClientState.Disconnected && transport.ConnectAttempts == 2);
		client.State.Should().Be(GateClientState.Disconnected);

		transport.AlwaysFail = false;
		await client.StartAsync();

		client.State.Should().Be(GateClientState.Connected);
		await client.StopAsync();
	}

	[Fact]
	public async Task SelectConnectedProxyRoundRobinSkipsDisconnected()
	{
		var transport = new FakeGateClientTransport();
		var manager = new GateClientManager(["a", "b", "c"], transport);
		await manager.StartAsync();

		// gate "b" goes down. The client schedules an immediate reconnect on remote disconnect, so
		// fail subsequent connects to keep it down; the other two gates stay connected.
		transport.AlwaysFail = true;
		transport.Connections.Single(c => c.ConnectionId == "Gate1-2").Disconnect();
		await WaitUntilAsync(() => manager.Proxies[1].IsConnected == false);

		var picks = new List<string?>();
		for (var i = 0; i < 4; i++)
		{
			picks.Add(manager.SelectConnectedProxy()!.ConnectionId);
		}

		picks.Should().Equal(["Gate0-1", "Gate2-3", "Gate0-1", "Gate2-3"]);

		await manager.StopAsync();
	}

	[Fact]
	public async Task SelectConnectedProxyReturnsNullForEmptyPool()
	{
		var transport = new FakeGateClientTransport();
		var manager = new GateClientManager([], transport);

		manager.SelectConnectedProxy().Should().BeNull();

		await manager.StartAsync();
		manager.SelectConnectedProxy().Should().BeNull();
		manager.Clients.Should().BeEmpty();

		await manager.StopAsync();
	}

	[Fact]
	public async Task SendThroughAnyThrowsWhenNoGateConnected()
	{
		var transport = new FakeGateClientTransport { AlwaysFail = true };
		var manager = new GateClientManager(["gate-a"], transport);
		await manager.StartAsync();
		await WaitUntilAsync(() => manager.Clients[0].State == GateClientState.Disconnected);

		await FluentActions.Awaiting(() => manager.SendThroughAnyAsync(new byte[1]))
			.Should().ThrowAsync<InvalidOperationException>();

		await manager.StopAsync();
	}

	[Fact]
	public async Task AddGateWithChangedAddressReplacesConnection()
	{
		var transport = new FakeGateClientTransport();
		var manager = new GateClientManager(["gate-a"], transport);
		await manager.StartAsync();
		var stale = transport.Connections.Should().ContainSingle().Subject;

		// Same name, same address: idempotent no-op.
		await manager.AddGateAsync(new GateEndpoint("Gate0", "gate-a"));
		transport.Connections.Should().ContainSingle();

		// Same name, new address: stale connection replaced.
		await manager.AddGateAsync(new GateEndpoint("Gate0", "gate-b"));
		await WaitUntilAsync(() => transport.Connections.Count == 2);

		stale.Disposed.Should().BeTrue();
		manager.Clients.Should().ContainSingle();
		manager.Clients[0].Endpoint.Address.Should().Be("gate-b");
		manager.Clients[0].State.Should().Be(GateClientState.Connected);
		manager.Proxies[0].IsConnected.Should().BeTrue();

		await manager.StopAsync();
	}

	[Fact]
	public async Task RemoveGateStopsItsConnection()
	{
		var transport = new FakeGateClientTransport();
		var manager = new GateClientManager(["gate-a", "gate-b"], transport);
		await manager.StartAsync();

		await manager.RemoveGateAsync("Gate0");
		await WaitUntilAsync(() => transport.Connections[0].Disposed);

		manager.Clients.Should().ContainSingle();
		manager.Clients[0].Endpoint.Name.Should().Be("Gate1");

		await manager.StopAsync();
	}

	[Fact]
	public async Task ConnectedEventFiresPerSuccessfulConnect()
	{
		var transport = new FakeGateClientTransport();
		var connected = new List<string>();
		var client = new GateClient(
			new GateEndpoint("Gate0", "gate"),
			transport,
			new ReconnectPolicy(maxAttempts: 2, initialDelay: TimeSpan.Zero),
			NoopReconnectDelayStrategy.Instance);
		client.Connected += (_, e) => connected.Add(e.Connection.ConnectionId);

		await client.StartAsync();
		transport.Connections.Single().Disconnect(new InvalidOperationException("remote close"));
		await WaitUntilAsync(() => transport.ConnectAttempts >= 2);

		connected.Should().HaveCount(2);
		connected[1].Should().Be(client.Proxy.ConnectionId);

		await client.StopAsync();
	}

	[Fact]
	public async Task TcpPoolRoundTripsWithGateServer()
	{
		await using var system = new ActorSystem();
		var options = new GateServerOptions
		{
			TcpPort = 0,
			EnableWebSockets = false,
			RouterFactory = _ => new EchoSessionRouter()
		};
		await using var gate = new GateServer(system, options, NullLogger<GateServer>.Instance);
		await gate.StartAsync();
		var tcpEndpoint = gate.TcpEndpoint!;
		tcpEndpoint.Should().NotBeNull();

		var transport = new TcpGateClientTransport();
		var manager = new GateClientManager([$"{tcpEndpoint.Address}:{tcpEndpoint.Port}"], transport);
		await manager.StartAsync();

		// Wait for the connection to be installed and the TCP session registered server-side, then
		// send through the pool and observe the reply on the connection's inbound FrameReceived event.
		await WaitUntilAsync(() => manager.Proxies[0].IsConnected);
		var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		var connection = (TcpGateProxyConnection)manager.Proxies[0].CurrentConnection!;
		connection.FrameReceived += (_, e) => received.TrySetResult(Encoding.UTF8.GetString(e.Payload.Span));

		await manager.SendThroughAnyAsync(Encoding.UTF8.GetBytes("hello"));

		var reply = await received.Task.WaitAsync(WaitTimeout);
		reply.Should().Be("hello");

		await manager.StopAsync();
		await gate.StopAsync();
	}

	private static async ValueTask WaitUntilAsync(Func<bool> predicate)
	{
		using var cts = new CancellationTokenSource(WaitTimeout);
		while (!predicate())
		{
			cts.Token.ThrowIfCancellationRequested();
			await Task.Delay(10, cts.Token);
		}
	}

	private sealed class FakeGateClientTransport : IGateClientTransport
	{
		public int ConnectAttempts { get; private set; }

		public bool AlwaysFail { get; set; }

		public List<GateEndpoint> ConnectEndpoints { get; } = [];

		public List<FakeGateProxyConnection> Connections { get; } = [];

		public Task<IGateProxyConnection> ConnectAsync(GateEndpoint endpoint, CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ConnectAttempts++;
			ConnectEndpoints.Add(endpoint);

			if (AlwaysFail)
			{
				throw new InvalidOperationException("connect failed");
			}

			var connection = new FakeGateProxyConnection($"{endpoint.Name}-{ConnectAttempts}");
			Connections.Add(connection);
			return Task.FromResult<IGateProxyConnection>(connection);
		}
	}

	private sealed class FakeGateProxyConnection : IGateProxyConnection
	{
		public FakeGateProxyConnection(string connectionId)
		{
			ConnectionId = connectionId;
		}

		public string ConnectionId { get; }

		public bool IsConnected { get; private set; } = true;

		public bool Disposed { get; private set; }

		public event EventHandler<GateDisconnectedEventArgs>? Disconnected;

		public event EventHandler<GateFrameReceivedEventArgs>? FrameReceived;

		public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
		{
			if (!IsConnected)
			{
				throw new InvalidOperationException("connection closed");
			}

			return Task.CompletedTask;
		}

		public void Disconnect(Exception? exception = null)
		{
			IsConnected = false;
			Disconnected?.Invoke(this, new GateDisconnectedEventArgs(exception));
		}

		public void Receive(ReadOnlyMemory<byte> payload)
		{
			FrameReceived?.Invoke(this, new GateFrameReceivedEventArgs(payload));
		}

		public ValueTask DisposeAsync()
		{
			Disposed = true;
			IsConnected = false;
			return ValueTask.CompletedTask;
		}
	}

	private sealed class BlockingDelayStrategy : IReconnectDelayStrategy
	{
		private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public bool Cancelled { get; private set; }

		public async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
		{
			_ = delay;
			_entered.TrySetResult();
			try
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			}
			catch (OperationCanceledException)
			{
				Cancelled = true;
				throw;
			}
		}

		public async Task WaitForDelayAsync()
		{
			await _entered.Task.WaitAsync(WaitTimeout);
		}
	}

	private sealed class EchoSessionRouter : ISessionMessageRouter
	{
		private SessionContext? _context;

		public SessionMetadata Metadata => _context!.Metadata;

		public Task<(SessionCloseReason Reason, string? Description)> Closed => Task.FromResult((SessionCloseReason.ClientDisconnected, (string?)null));

		public Task OnSessionStartedAsync(SessionContext context, CancellationToken cancellationToken)
		{
			_context = context;
			return Task.CompletedTask;
		}

		public async Task OnSessionMessageAsync(SessionContext context, ReadOnlyMemory<byte> payload,
			CancellationToken cancellationToken)
		{
			await context.SendAsync(payload.Span.ToArray(), cancellationToken).ConfigureAwait(false);
		}

		public Task OnSessionClosedAsync(SessionContext context, SessionCloseReason reason, string? description,
			CancellationToken cancellationToken)
		{
			return Task.CompletedTask;
		}
	}
}

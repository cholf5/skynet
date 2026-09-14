using System;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Skynet.Cluster;
using Skynet.Core;

namespace Skynet.Cluster.Tests;

public sealed class RedisClusterRegistryTests
{
	[Fact]
	public async Task ShouldRegisterAndResolveAcrossNodes()
	{
		var server = new FakeRedisServer();
		var optionsA = CreateOptions("node-a", 5000);
		var optionsB = CreateOptions("node-b", 6000);

		await using var registryA = new RedisClusterRegistry(optionsA, server.CreateClient(), NullLoggerFactory.Instance);
		await using var registryB = new RedisClusterRegistry(optionsB, server.CreateClient(), NullLoggerFactory.Instance);

		var handle = new ActorHandle(42);
		registryA.RegisterLocalActor("service", handle);

		registryB.TryResolveByName("service", out var location).Should().BeTrue();
		location.NodeId.Should().Be(optionsA.NodeId);
		location.Handle.Should().Be(handle);

		registryB.TryResolveByHandle(handle, out var handleLocation).Should().BeTrue();
		handleLocation.NodeId.Should().Be(optionsA.NodeId);

		registryB.TryGetNode(optionsA.NodeId, out var descriptor).Should().BeTrue();
		descriptor.EndPoint.Port.Should().Be(optionsA.LocalEndPoint.Port);
	}

	[Fact]
	public async Task ShouldPreventDuplicateServiceRegistration()
	{
		var server = new FakeRedisServer();
		var optionsA = CreateOptions("node-a", 5000);
		var optionsB = CreateOptions("node-b", 6000);

		await using var registryA = new RedisClusterRegistry(optionsA, server.CreateClient(), NullLoggerFactory.Instance);
		await using var registryB = new RedisClusterRegistry(optionsB, server.CreateClient(), NullLoggerFactory.Instance);

		registryA.RegisterLocalActor("unique", new ActorHandle(100));

		Action action = () => registryB.RegisterLocalActor("unique", new ActorHandle(200));
		action.Should().Throw<InvalidOperationException>();
	}

	[Fact]
	public async Task UnregisterShouldRemoveEntriesAndPublishInvalidation()
	{
		var server = new FakeRedisServer();
		var optionsA = CreateOptions("node-a", 5000);
		var optionsB = CreateOptions("node-b", 6000);

		await using var registryA = new RedisClusterRegistry(optionsA, server.CreateClient(), NullLoggerFactory.Instance);
		await using var registryB = new RedisClusterRegistry(optionsB, server.CreateClient(), NullLoggerFactory.Instance);

		var handle = new ActorHandle(777);
		registryA.RegisterLocalActor("temp", handle);
		registryA.UnregisterLocalActor("temp", handle);

		registryB.TryResolveByName("temp", out _).Should().BeFalse();
		registryB.TryResolveByHandle(handle, out _).Should().BeFalse();
	}

	[Fact]
	public async Task ReconnectShouldReplayAddedAndRemovedServices()
	{
		var server = new FakeRedisServer();
		var optionsA = CreateOptions("node-a", 5000);
		var optionsB = CreateOptions("node-b", 6000);
		var loggerB = new CapturingLogger();
		var clientB = server.CreateClient();

		await using var registryA = new RedisClusterRegistry(optionsA, server.CreateClient(), NullLoggerFactory.Instance);
		await using var registryB = new RedisClusterRegistry(optionsB, clientB, loggerB);

		var removedHandle = new ActorHandle(777);
		var addedHandle = new ActorHandle(888);
		registryA.RegisterLocalActor("removed-svc", removedHandle);
		registryB.TryResolveByName("removed-svc", out _).Should().BeTrue();

		// While node B's subscription is disconnected, node A registers a new service and unregisters the old one.
		clientB.SimulateConnectionFailure();
		clientB.IsConnected.Should().BeFalse();
		registryA.RegisterLocalActor("added-svc", addedHandle);
		registryA.UnregisterLocalActor("removed-svc", removedHandle);

		// On reconnect, the reconciliation diff must replay both changes as synthetic events.
		clientB.SimulateReconnect();

		await TestWait.UntilAsync(() => loggerB.Snapshot.Any(entry =>
			entry.Message.Contains("replayed 1 added, 1 removed, 0 suppressed.", StringComparison.Ordinal)));

		// The replayed events must have been delivered to the existing event subscriber (the cache handler).
		registryB.TryResolveByName("added-svc", out var added).Should().BeTrue();
		added.Handle.Should().Be(addedHandle);
		registryB.TryResolveByName("removed-svc", out _).Should().BeFalse();
		registryB.TryResolveByHandle(removedHandle, out _).Should().BeFalse();
	}

	[Fact]
	public async Task ReconnectShouldSuppressUnchangedKeys()
	{
		var server = new FakeRedisServer();
		var optionsA = CreateOptions("node-a", 5000);
		var optionsB = CreateOptions("node-b", 6000);
		var loggerB = new CapturingLogger();
		var clientB = server.CreateClient();

		await using var registryA = new RedisClusterRegistry(optionsA, server.CreateClient(), NullLoggerFactory.Instance);
		await using var registryB = new RedisClusterRegistry(optionsB, clientB, loggerB);

		registryA.RegisterLocalActor("stable-svc", new ActorHandle(321));
		registryB.TryResolveByName("stable-svc", out _).Should().BeTrue();

		// A full disconnect/reconnect cycle without any registry change must not replay any event.
		clientB.SimulateConnectionFailure();
		clientB.SimulateReconnect();

		await TestWait.UntilAsync(() => loggerB.Snapshot.Any(entry =>
			entry.Message.Contains("replayed 0 added, 0 removed, 1 suppressed.", StringComparison.Ordinal)));

		registryB.TryResolveByName("stable-svc", out var stable).Should().BeTrue();
		stable.Handle.Value.Should().Be(321);
	}

	[Fact]
	public async Task ShouldSelfHealAfterKeyExpiry()
	{
		var server = new FakeRedisServer();
		var optionsA = CreateOptions("node-a", 5000);
		var optionsB = CreateOptions("node-b", 6000);
		var loggerA = new CapturingLogger();

		await using var registryA = new RedisClusterRegistry(optionsA, server.CreateClient(), loggerA);
		await using var registryB = new RedisClusterRegistry(optionsB, server.CreateClient(), NullLoggerFactory.Instance);

		var handle = new ActorHandle(42);
		registryA.RegisterLocalActor("heal", handle);
		registryB.TryResolveByName("heal", out _).Should().BeTrue();

		// Force the service and handle keys to expire (TTL loss). The next heartbeat must detect the loss
		// and re-register both keys from the stored value copies.
		server.ForceExpireKey("skynet:cluster:services:heal");
		server.ForceExpireKey("skynet:cluster:handles:42");

		// First the entry must disappear (local cache expires, direct read misses), proving the key is gone.
		await TestWait.UntilAsync(() => !registryB.TryResolveByName("heal", out _));

		// Then the heartbeat self-heal must re-register the keys with the original value copy.
		await TestWait.UntilAsync(() =>
		{
			if (!registryB.TryResolveByName("heal", out var healed))
			{
				return false;
			}

			return healed.NodeId == optionsA.NodeId && healed.Handle == handle;
		});

		loggerA.Snapshot.Any(entry =>
			entry.Level == LogLevel.Information &&
			entry.Message.Contains("self-heal", StringComparison.OrdinalIgnoreCase))
			.Should().BeTrue();

		registryB.TryResolveByHandle(handle, out var byHandle).Should().BeTrue();
		byHandle.NodeId.Should().Be(optionsA.NodeId);
	}

	[Fact]
	public async Task ConcurrentReconnectTriggersShouldReplayDiffExactlyOnce()
	{
		// SE.Redis raises ConnectionRestored once per connection (interactive + subscription), so
		// two triggers can race. Two concurrent reconciliations must not replay the same diff
		// twice: one pass runs, and a trigger arriving mid-pass schedules a follow-up that finds
		// the diff already consumed (0 added / 0 removed).
		var server = new FakeRedisServer();
		var optionsA = CreateOptions("node-a", 5000);
		var optionsB = CreateOptions("node-b", 6000);
		var loggerB = new CapturingLogger();
		var clientB = server.CreateClient();

		await using var registryA = new RedisClusterRegistry(optionsA, server.CreateClient(), NullLoggerFactory.Instance);
		await using var registryB = new RedisClusterRegistry(optionsB, clientB, loggerB);

		var handle = new ActorHandle(555);
		registryA.RegisterLocalActor("reconcile-svc", handle);
		registryB.TryResolveByName("reconcile-svc", out _).Should().BeTrue();

		clientB.SimulateConnectionFailure();
		registryA.RegisterLocalActor("mid-outage-svc", new ActorHandle(556));

		// Two concurrent restore triggers, mirroring the interactive + subscription connections.
		var first = Task.Run(clientB.SimulateReconnect);
		var second = Task.Run(clientB.SimulateReconnect);
		await Task.WhenAll(first, second);

		// The diff must be replayed exactly once (1 added, 0 removed).
		await TestWait.UntilAsync(() => loggerB.Snapshot.Any(entry =>
			entry.Message.Contains("replayed 1 added, 0 removed, 1 suppressed.", StringComparison.Ordinal)));

		// Give any scheduled follow-up pass time to run, then verify no duplicate replay happened
		// and the reconciled state is visible to subscribers.
		await Task.Delay(200);
		loggerB.Snapshot.Count(entry =>
			entry.Message.Contains("replayed 1 added, 0 removed, 1 suppressed.", StringComparison.Ordinal))
			.Should().Be(1, "the reconciliation diff must be replayed exactly once");
		registryB.TryResolveByName("mid-outage-svc", out var midOutage).Should().BeTrue();
		midOutage.Handle.Value.Should().Be(556);
	}

	private static RedisClusterRegistryOptions CreateOptions(string nodeId, int port)
	{
		return new RedisClusterRegistryOptions
		{
			ConnectionString = "fake",
			NodeId = nodeId,
			LocalEndPoint = new IPEndPoint(IPAddress.Loopback, port),
			RegistrationTtl = TimeSpan.FromSeconds(5),
			HeartbeatInterval = TimeSpan.FromMilliseconds(100),
			CacheTtl = TimeSpan.FromMilliseconds(50)
		};
	}
}

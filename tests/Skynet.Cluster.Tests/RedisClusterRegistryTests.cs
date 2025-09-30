using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
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

	private sealed class FakeRedisServer
	{
		private readonly object _gate = new();
		private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
		private readonly ConcurrentDictionary<string, List<Action<string>>> _subscriptions = new(StringComparer.Ordinal);

		public IRedisClient CreateClient()
		{
			return new FakeRedisClient(this);
		}

		internal bool TryClaimKey(string key, string value, TimeSpan ttl, out string? existingValue)
		{
			lock (_gate)
			{
				if (_entries.TryGetValue(key, out var entry))
				{
					if (!entry.IsExpired)
					{
						existingValue = entry.Value;
						return false;
					}

					_entries.Remove(key);
				}

				_entries[key] = Entry.Create(value, ttl);
				existingValue = null;
				return true;
			}
		}

		internal void SetString(string key, string value, TimeSpan ttl)
		{
			lock (_gate)
			{
				_entries[key] = Entry.Create(value, ttl);
			}
		}

		internal string? GetString(string key)
		{
			lock (_gate)
			{
				if (!_entries.TryGetValue(key, out var entry))
				{
					return null;
				}

				if (entry.IsExpired)
				{
					_entries.Remove(key);
					return null;
				}

				return entry.Value;
			}
		}

		internal bool KeyExpire(string key, TimeSpan ttl)
		{
			lock (_gate)
			{
				if (_entries.TryGetValue(key, out var entry) && !entry.IsExpired)
				{
					_entries[key] = entry.Refresh(ttl);
					return true;
				}

				_entries.Remove(key);
				return false;
			}
		}

		internal bool KeyDelete(string key)
		{
			lock (_gate)
			{
				return _entries.Remove(key);
			}
		}

		internal void Publish(string channel, string message)
		{
			if (_subscriptions.TryGetValue(channel, out var handlers))
			{
				Action<string>[] snapshot;
				lock (handlers)
				{
					snapshot = handlers.ToArray();
				}

				foreach (var handler in snapshot)
				{
					handler(message);
				}
			}
		}

		internal IDisposable Subscribe(string channel, Action<string> handler)
		{
			var list = _subscriptions.GetOrAdd(channel, _ => new List<Action<string>>());
			lock (list)
			{
				list.Add(handler);
			}

			return new Subscription(this, channel, handler);
		}

		private void Unsubscribe(string channel, Action<string> handler)
		{
			if (_subscriptions.TryGetValue(channel, out var list))
			{
				lock (list)
				{
					list.Remove(handler);
				}
			}
		}

		private readonly struct Entry
		{
			private Entry(string value, DateTimeOffset expiry)
			{
				Value = value;
				Expiry = expiry;
			}

			public string Value { get; }
			public DateTimeOffset Expiry { get; }
			public bool IsExpired => DateTimeOffset.UtcNow > Expiry;

			public static Entry Create(string value, TimeSpan ttl)
			{
				return new Entry(value, DateTimeOffset.UtcNow.Add(ttl));
			}

			public Entry Refresh(TimeSpan ttl)
			{
				return new Entry(Value, DateTimeOffset.UtcNow.Add(ttl));
			}
		}

		private sealed class Subscription : IDisposable
		{
			private readonly FakeRedisServer _server;
			private readonly string _channel;
			private readonly Action<string> _handler;
			private bool _disposed;

			public Subscription(FakeRedisServer server, string channel, Action<string> handler)
			{
				_server = server;
				_channel = channel;
				_handler = handler;
			}

			public void Dispose()
			{
				if (_disposed)
				{
					return;
				}

				_server.Unsubscribe(_channel, _handler);
				_disposed = true;
			}
		}
	}

	private sealed class FakeRedisClient : IRedisClient
	{
		private readonly FakeRedisServer _server;

		public FakeRedisClient(FakeRedisServer server)
		{
			_server = server;
		}

		public bool TryClaimKey(string key, string value, TimeSpan ttl, out string? existingValue)
		{
			return _server.TryClaimKey(key, value, ttl, out existingValue);
		}

		public void SetString(string key, string value, TimeSpan ttl)
		{
			_server.SetString(key, value, ttl);
		}

		public string? GetString(string key)
		{
			return _server.GetString(key);
		}

		public bool KeyExpire(string key, TimeSpan ttl)
		{
			return _server.KeyExpire(key, ttl);
		}

		public bool KeyDelete(string key)
		{
			return _server.KeyDelete(key);
		}

		public void Publish(string channel, string message)
		{
			_server.Publish(channel, message);
		}

		public IDisposable Subscribe(string channel, Action<string> handler)
		{
			return _server.Subscribe(channel, handler);
		}

		public ValueTask DisposeAsync()
		{
			return ValueTask.CompletedTask;
		}
	}
}

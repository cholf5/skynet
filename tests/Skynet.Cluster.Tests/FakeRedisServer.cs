using System.Collections.Concurrent;
using Skynet.Cluster;

namespace Skynet.Cluster.Tests;

/// <summary>
/// Full in-memory Redis fake shared by the cluster registry tests. It supports TTL-based expiry, forced
/// expiry (simulating a lost/evicted key), per-client connection failure and reconnect with automatic
/// re-subscription (mirroring StackExchange.Redis behaviour), and delivery only to connected subscribers.
/// Unit tests therefore run without a real Redis instance.
/// </summary>
internal sealed class FakeRedisServer
{
	private readonly object _gate = new();
	private readonly Dictionary<string, FakeRedisEntry> _entries = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, List<SubscriptionRecord>> _subscriptions = new(StringComparer.Ordinal);

	public FakeRedisClient CreateClient()
	{
		return new FakeRedisClient(this);
	}

	/// <summary>
	/// Removes a key immediately, regardless of its TTL - simulates TTL expiry, eviction, or deletion by
	/// another actor so the registry's self-heal path can be exercised deterministically.
	/// </summary>
	public void ForceExpireKey(string key)
	{
		ArgumentException.ThrowIfNullOrEmpty(key);
		lock (_gate)
		{
			_entries.Remove(key);
		}
	}

	internal bool TryClaimKey(string key, string value, TimeSpan ttl, out string? existingValue)
	{
		ArgumentException.ThrowIfNullOrEmpty(key);
		ArgumentNullException.ThrowIfNull(value);
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

			_entries[key] = FakeRedisEntry.Create(value, ttl);
			existingValue = null;
			return true;
		}
	}

	internal void SetString(string key, string value, TimeSpan ttl)
	{
		ArgumentException.ThrowIfNullOrEmpty(key);
		ArgumentNullException.ThrowIfNull(value);
		lock (_gate)
		{
			_entries[key] = FakeRedisEntry.Create(value, ttl);
		}
	}

	internal string? GetString(string key)
	{
		ArgumentException.ThrowIfNullOrEmpty(key);
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
		ArgumentException.ThrowIfNullOrEmpty(key);
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
		ArgumentException.ThrowIfNullOrEmpty(key);
		lock (_gate)
		{
			return _entries.Remove(key);
		}
	}

	internal IReadOnlyDictionary<string, string> ReadByPrefix(string prefix)
	{
		ArgumentException.ThrowIfNullOrEmpty(prefix);
		lock (_gate)
		{
			var results = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var pair in _entries)
			{
				if (pair.Key.StartsWith(prefix, StringComparison.Ordinal) && !pair.Value.IsExpired)
				{
					results[pair.Key] = pair.Value.Value;
				}
			}

			return results;
		}
	}

	internal void Publish(string channel, string message)
	{
		ArgumentException.ThrowIfNullOrEmpty(channel);
		ArgumentNullException.ThrowIfNull(message);
		if (!_subscriptions.TryGetValue(channel, out var records))
		{
			return;
		}

		SubscriptionRecord[] snapshot;
		lock (records)
		{
			snapshot = records.ToArray();
		}

		foreach (var record in snapshot)
		{
			record.Handler(message);
		}
	}

	internal void AttachSubscriptions(IReadOnlyList<SubscriptionRecord> records)
	{
		ArgumentNullException.ThrowIfNull(records);
		foreach (var record in records)
		{
			var list = _subscriptions.GetOrAdd(record.Channel, _ => new List<SubscriptionRecord>());
			lock (list)
			{
				list.Add(record);
			}
		}
	}

	internal void DetachSubscriptions(IReadOnlyList<SubscriptionRecord> records)
	{
		ArgumentNullException.ThrowIfNull(records);
		foreach (var record in records)
		{
			if (_subscriptions.TryGetValue(record.Channel, out var list))
			{
				lock (list)
				{
					list.Remove(record);
				}
			}
		}
	}
}

internal sealed record SubscriptionRecord(string Channel, Action<string> Handler);

/// <summary>
/// Thrown by <see cref="FakeRedisClient"/> for key operations while the simulated connection is down,
/// mirroring how StackExchange.Redis surfaces synchronous operations on a dropped connection.
/// </summary>
internal sealed class FakeRedisConnectionException : Exception
{
	public FakeRedisConnectionException()
		: base("The fake Redis connection is down.")
	{
	}
}

/// <summary>
/// A single logical client connection to the <see cref="FakeRedisServer"/> with its own connection state,
/// simulating an independent StackExchange.Redis multiplexer per client.
/// </summary>
internal sealed class FakeRedisClient : IRedisClient
{
	private readonly FakeRedisServer _server;
	private readonly object _gate = new();
	private readonly List<SubscriptionRecord> _subscriptions = new();
	private bool _connected;

	public event Action? ConnectionLost;

	public event Action? ConnectionRestored;

	public FakeRedisClient(FakeRedisServer server)
	{
		_server = server;
		_connected = true;
	}

	public bool IsConnected
	{
		get
		{
			lock (_gate)
			{
				return _connected;
			}
		}
	}

	/// <summary>
	/// Simulates a dropped connection: key operations start throwing, the subscription handlers are detached
	/// from the server (published messages while disconnected are lost), and <see cref="ConnectionLost"/> fires.
	/// </summary>
	public void SimulateConnectionFailure()
	{
		List<SubscriptionRecord> detached;
		lock (_gate)
		{
			if (!_connected)
			{
				return;
			}

			_connected = false;
			detached = new List<SubscriptionRecord>(_subscriptions);
		}

		_server.DetachSubscriptions(detached);
		ConnectionLost?.Invoke();
	}

	/// <summary>
	/// Simulates a recovered connection: the previously registered subscriptions are re-attached
	/// (StackExchange.Redis re-subscribes automatically on reconnect) and <see cref="ConnectionRestored"/> fires.
	/// </summary>
	public void SimulateReconnect()
	{
		List<SubscriptionRecord> attached;
		lock (_gate)
		{
			if (_connected)
			{
				return;
			}

			_connected = true;
			attached = new List<SubscriptionRecord>(_subscriptions);
		}

		_server.AttachSubscriptions(attached);
		ConnectionRestored?.Invoke();
	}

	public bool TryClaimKey(string key, string value, TimeSpan ttl, out string? existingValue)
	{
		EnsureConnected();
		return _server.TryClaimKey(key, value, ttl, out existingValue);
	}

	public void SetString(string key, string value, TimeSpan ttl)
	{
		EnsureConnected();
		_server.SetString(key, value, ttl);
	}

	public string? GetString(string key)
	{
		EnsureConnected();
		return _server.GetString(key);
	}

	public bool KeyExpire(string key, TimeSpan ttl)
	{
		EnsureConnected();
		return _server.KeyExpire(key, ttl);
	}

	public bool KeyDelete(string key)
	{
		EnsureConnected();
		return _server.KeyDelete(key);
	}

	public IReadOnlyDictionary<string, string> ReadByPrefix(string prefix)
	{
		EnsureConnected();
		return _server.ReadByPrefix(prefix);
	}

	public void Publish(string channel, string message)
	{
		// Fire-and-forget: dropped silently while disconnected (same as CommandFlags.FireAndForget).
		if (!IsConnected)
		{
			return;
		}

		_server.Publish(channel, message);
	}

	public IDisposable Subscribe(string channel, Action<string> handler)
	{
		ArgumentException.ThrowIfNullOrEmpty(channel);
		ArgumentNullException.ThrowIfNull(handler);
		var record = new SubscriptionRecord(channel, handler);
		bool isConnected;
		lock (_gate)
		{
			_subscriptions.Add(record);
			isConnected = _connected;
		}

		if (isConnected)
		{
			_server.AttachSubscriptions(new[] { record });
		}

		return new Subscription(this, record);
	}

	public ValueTask DisposeAsync()
	{
		List<SubscriptionRecord> detached;
		lock (_gate)
		{
			if (_subscriptions.Count == 0 && !_connected)
			{
				return ValueTask.CompletedTask;
			}

			detached = new List<SubscriptionRecord>(_subscriptions);
			_subscriptions.Clear();
			_connected = false;
		}

		_server.DetachSubscriptions(detached);
		return ValueTask.CompletedTask;
	}

	private void EnsureConnected()
	{
		if (!IsConnected)
		{
			throw new FakeRedisConnectionException();
		}
	}

	private void RemoveSubscription(SubscriptionRecord record)
	{
		bool wasConnected;
		lock (_gate)
		{
			_subscriptions.Remove(record);
			wasConnected = _connected;
		}

		if (wasConnected)
		{
			_server.DetachSubscriptions(new[] { record });
		}
	}

	private sealed class Subscription(FakeRedisClient client, SubscriptionRecord record) : IDisposable
	{
		private bool _disposed;

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			client.RemoveSubscription(record);
			_disposed = true;
		}
	}
}

/// <summary>
/// An entry stored in the <see cref="FakeRedisServer"/> with its absolute expiry, simulating Redis key TTLs.
/// </summary>
internal readonly record struct FakeRedisEntry(string Value, DateTimeOffset Expiry)
{
	public bool IsExpired => DateTimeOffset.UtcNow > Expiry;

	public static FakeRedisEntry Create(string value, TimeSpan ttl)
	{
		return new FakeRedisEntry(value, DateTimeOffset.UtcNow.Add(ttl));
	}

	public FakeRedisEntry Refresh(TimeSpan ttl)
	{
		return new FakeRedisEntry(Value, DateTimeOffset.UtcNow.Add(ttl));
	}
}

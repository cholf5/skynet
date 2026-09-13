using StackExchange.Redis;

namespace Skynet.Cluster;

internal interface IRedisClient : IAsyncDisposable
{
	/// <summary>
	/// Raised when the underlying connection drops. This includes every connection type managed by the client
	/// (interactive and subscription), mirroring <see cref="ConnectionMultiplexer.ConnectionFailed"/>.
	/// </summary>
	event Action? ConnectionLost;

	/// <summary>
	/// Raised when a previously dropped underlying connection has been restored, mirroring
	/// <see cref="ConnectionMultiplexer.ConnectionRestored"/>.
	/// </summary>
	event Action? ConnectionRestored;

	bool TryClaimKey(string key, string value, TimeSpan ttl, out string? existingValue);

	void SetString(string key, string value, TimeSpan ttl);

	string? GetString(string key);

	bool KeyExpire(string key, TimeSpan ttl);

	bool KeyDelete(string key);

	void Publish(string channel, string message);

	IDisposable Subscribe(string channel, Action<string> handler);

	/// <summary>
	/// Reads all non-expired string keys whose name starts with <paramref name="prefix"/> (full scan).
	/// May throw when the connection is down; callers are expected to treat this as a failed reconciliation
	/// attempt and retry on the next reconnect.
	/// </summary>
	IReadOnlyDictionary<string, string> ReadByPrefix(string prefix);
}

internal sealed class StackExchangeRedisClient : IRedisClient
{
	private readonly ConnectionMultiplexer _connection;
	private readonly IDatabase _database;
	private readonly ISubscriber _subscriber;
	private bool _disposed;

	public event Action? ConnectionLost;

	public event Action? ConnectionRestored;

	public StackExchangeRedisClient(RedisClusterRegistryOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		if (string.IsNullOrWhiteSpace(options.ConnectionString))
		{
			throw new ArgumentException("Connection string must be provided.", nameof(options));
		}

		var configuration = ConfigurationOptions.Parse(options.ConnectionString);
		configuration.AbortOnConnectFail = false;
		configuration.ClientName = $"skynet-cluster-{options.NodeId}";
		_connection = ConnectionMultiplexer.Connect(configuration);
		_database = _connection.GetDatabase(options.Database);
		_subscriber = _connection.GetSubscriber();

		// Wire the real disconnect signals: ConnectionFailed fires for every dropped connection type
		// (interactive and subscription connections included) and ConnectionRestored fires once the
		// connection recovers. These are production paths - not test-only hooks.
		_connection.ConnectionFailed += OnConnectionFailed;
		_connection.ConnectionRestored += OnConnectionRestored;
	}

	private void OnConnectionFailed(object? sender, ConnectionFailedEventArgs args)
	{
		ConnectionLost?.Invoke();
	}

	private void OnConnectionRestored(object? sender, ConnectionFailedEventArgs args)
	{
		ConnectionRestored?.Invoke();
	}

	public bool TryClaimKey(string key, string value, TimeSpan ttl, out string? existingValue)
	{
		ArgumentException.ThrowIfNullOrEmpty(key);
		ArgumentNullException.ThrowIfNull(value);
		var claimed = _database.StringSet(key, value, ttl, When.NotExists);
		if (claimed)
		{
			existingValue = null;
			return true;
		}

		var current = _database.StringGet(key);
		existingValue = current.IsNullOrEmpty ? null : current.ToString();
		return false;
	}

	public void SetString(string key, string value, TimeSpan ttl)
	{
		ArgumentException.ThrowIfNullOrEmpty(key);
		ArgumentNullException.ThrowIfNull(value);
		_database.StringSet(key, value, ttl);
	}

	public string? GetString(string key)
	{
		ArgumentException.ThrowIfNullOrEmpty(key);
		var value = _database.StringGet(key);
		return value.IsNullOrEmpty ? null : value.ToString();
	}

	public bool KeyExpire(string key, TimeSpan ttl)
	{
		ArgumentException.ThrowIfNullOrEmpty(key);
		return _database.KeyExpire(key, ttl);
	}

	public bool KeyDelete(string key)
	{
		ArgumentException.ThrowIfNullOrEmpty(key);
		return _database.KeyDelete(key);
	}

	public void Publish(string channel, string message)
	{
		ArgumentException.ThrowIfNullOrEmpty(channel);
		ArgumentNullException.ThrowIfNull(message);
		_subscriber.Publish(RedisChannel.Literal(channel), message, CommandFlags.FireAndForget);
	}

	public IReadOnlyDictionary<string, string> ReadByPrefix(string prefix)
	{
		ArgumentException.ThrowIfNullOrEmpty(prefix);
		var results = new Dictionary<string, string>(StringComparer.Ordinal);
		var pattern = $"{prefix}*";
		foreach (var endpoint in _connection.GetEndPoints(configuredOnly: false))
		{
			var server = _connection.GetServer(endpoint);
			foreach (var key in server.Keys(_database.Database, pattern, 250))
			{
				var value = _database.StringGet(key);
				if (!value.IsNullOrEmpty)
				{
					results[key.ToString()] = value.ToString();
				}
			}
		}

		return results;
	}

	public IDisposable Subscribe(string channel, Action<string> handler)
	{
		ArgumentException.ThrowIfNullOrEmpty(channel);
		ArgumentNullException.ThrowIfNull(handler);
		var redisChannel = RedisChannel.Literal(channel);
		Action<RedisChannel, RedisValue> wrapped = (_, value) => handler(value!);
		_subscriber.SubscribeAsync(redisChannel, wrapped).GetAwaiter().GetResult();
		return new RedisSubscription(_subscriber, redisChannel, wrapped);
	}

	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		await _connection.DisposeAsync().ConfigureAwait(false);
	}

	private sealed class RedisSubscription(
		ISubscriber subscriber,
		RedisChannel channel,
		Action<RedisChannel, RedisValue> handler)
		: IDisposable
	{
		private bool _disposed;

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			subscriber.UnsubscribeAsync(channel, handler).GetAwaiter().GetResult();
			_disposed = true;
		}
	}
}

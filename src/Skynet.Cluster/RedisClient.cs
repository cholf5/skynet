using System;
using StackExchange.Redis;

namespace Skynet.Cluster;

internal interface IRedisClient : IAsyncDisposable
{
	bool TryClaimKey(string key, string value, TimeSpan ttl, out string? existingValue);

	void SetString(string key, string value, TimeSpan ttl);

	string? GetString(string key);

	bool KeyExpire(string key, TimeSpan ttl);

	bool KeyDelete(string key);

	void Publish(string channel, string message);

	IDisposable Subscribe(string channel, Action<string> handler);
}

internal sealed class StackExchangeRedisClient : IRedisClient
{
	private readonly ConnectionMultiplexer _connection;
	private readonly IDatabase _database;
	private readonly ISubscriber _subscriber;
	private bool _disposed;

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

	private sealed class RedisSubscription : IDisposable
	{
		private readonly ISubscriber _subscriber;
		private readonly RedisChannel _channel;
		private readonly Action<RedisChannel, RedisValue> _handler;
		private bool _disposed;

		public RedisSubscription(ISubscriber subscriber, RedisChannel channel, Action<RedisChannel, RedisValue> handler)
		{
			_subscriber = subscriber;
			_channel = channel;
			_handler = handler;
		}

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_subscriber.UnsubscribeAsync(_channel, _handler).GetAwaiter().GetResult();
			_disposed = true;
		}
	}
}

using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Skynet.Core;

namespace Skynet.Cluster;

/// <summary>
/// Provides a Redis-backed implementation of <see cref="IClusterRegistry"/>.
/// </summary>
public sealed class RedisClusterRegistry : IClusterRegistry, IAsyncDisposable
{
	private readonly RedisClusterRegistryOptions _options;
	private readonly IRedisClient _client;
	private readonly ILogger<RedisClusterRegistry> _logger;
	private readonly ConcurrentDictionary<string, CacheEntry> _nameCache = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<long, CacheEntry> _handleCache = new();
	private readonly ConcurrentDictionary<string, NodeCacheEntry> _nodeCache = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, LocalRegistration> _localServices = new(StringComparer.Ordinal);
	// Last state that has been delivered to event subscribers (service name -> encoded location). Used as the
	// diff baseline for reconciliation so unchanged keys can be suppressed as no-op replays.
	private readonly ConcurrentDictionary<string, string> _eventSnapshot = new(StringComparer.Ordinal);
	private readonly CancellationTokenSource _cts = new();
	private readonly Lock _localServicesLock = new();
	private readonly IDisposable _subscription;
	private readonly Task _heartbeatTask;
	private readonly string _nodeKey;
	private readonly string _encodedNodeEndpoint;
	private bool _disposed;

	/// <summary>
	/// Initializes a new instance of the <see cref="RedisClusterRegistry"/> class.
	/// </summary>
	public RedisClusterRegistry(RedisClusterRegistryOptions options, ILoggerFactory? loggerFactory = null)
		: this(options, new StackExchangeRedisClient(options), loggerFactory)
	{
	}

	internal RedisClusterRegistry(RedisClusterRegistryOptions options, IRedisClient client, ILoggerFactory? loggerFactory = null)
	{
		_options = options ?? throw new ArgumentNullException(nameof(options));
		_client = client ?? throw new ArgumentNullException(nameof(client));
		_logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<RedisClusterRegistry>();
		ValidateOptions(options);
		_nodeKey = NodeKey(options.NodeId);
		_encodedNodeEndpoint = EncodeEndpoint(options.LocalEndPoint);
		RegisterNode();
		_client.ConnectionRestored += OnConnectionRestored;
		_subscription = _client.Subscribe(GetEventChannel(), OnEventMessage);
		_heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
	}

	/// <inheritdoc />
	public string LocalNodeId => _options.NodeId;

	/// <inheritdoc />
	public bool TryResolveByName(string name, out ClusterActorLocation location)
	{
		ArgumentException.ThrowIfNullOrEmpty(name);
		ThrowIfDisposed();

		if (TryGetCachedLocation(name, out location))
		{
			return true;
		}

		var value = _client.GetString(ServiceKey(name));
		if (string.IsNullOrEmpty(value))
		{
			_nameCache.TryRemove(name, out _);
			location = null!;
			return false;
		}

		location = DecodeLocation(value);
		CacheLookup(name, location);
		EnsureNodeCached(location.NodeId);
		return true;
	}

	/// <inheritdoc />
	public bool TryResolveByHandle(ActorHandle handle, out ClusterActorLocation location)
	{
		if (!handle.IsValid)
		{
			location = null!;
			return false;
		}

		ThrowIfDisposed();

		if (TryGetCachedHandle(handle.Value, out location))
		{
			return true;
		}

		var value = _client.GetString(HandleKey(handle.Value));
		if (string.IsNullOrEmpty(value))
		{
			_handleCache.TryRemove(handle.Value, out _);
			location = null!;
			return false;
		}

		location = new ClusterActorLocation(value, handle);
		CacheHandle(handle.Value, location);
		EnsureNodeCached(location.NodeId);
		return true;
	}

	/// <inheritdoc />
	public bool TryGetNode(string nodeId, out ClusterNodeDescriptor descriptor)
	{
		ArgumentException.ThrowIfNullOrEmpty(nodeId);
		ThrowIfDisposed();

		if (TryGetCachedNode(nodeId, out descriptor))
		{
			return true;
		}

		var value = _client.GetString(NodeKey(nodeId));
		if (string.IsNullOrEmpty(value))
		{
			descriptor = null!;
			return false;
		}

		if (!TryParseEndpoint(value, out var endpoint))
		{
			descriptor = null!;
			return false;
		}

		descriptor = new ClusterNodeDescriptor(nodeId, endpoint);
		CacheNode(nodeId, descriptor);
		return true;
	}

	/// <inheritdoc />
	public void RegisterLocalActor(string name, ActorHandle handle)
	{
		ArgumentException.ThrowIfNullOrEmpty(name);
		if (!handle.IsValid)
		{
			throw new ArgumentException("Handle must be valid.", nameof(handle));
		}

		ThrowIfDisposed();

		lock (_localServicesLock)
		{
			if (_localServices.TryGetValue(name, out var existing) && existing.Handle != handle)
			{
				throw new InvalidOperationException($"Service '{name}' is already registered locally with handle {existing.Handle.Value}.");
			}

			var location = new ClusterActorLocation(_options.NodeId, handle);
			var encodedLocation = EncodeLocation(location);
			var expiry = _options.RegistrationTtl;
			var serviceKey = ServiceKey(name);

			var claimed = _client.TryClaimKey(serviceKey, encodedLocation, expiry, out var currentOwner);
			if (!claimed)
			{
				if (currentOwner is null)
				{
					claimed = _client.TryClaimKey(serviceKey, encodedLocation, expiry, out currentOwner);
				}

				if (!claimed && currentOwner is not null)
				{
					var remote = DecodeLocation(currentOwner);
					if (!string.Equals(remote.NodeId, _options.NodeId, StringComparison.Ordinal) || remote.Handle.Value != handle.Value)
					{
						throw new InvalidOperationException($"Service '{name}' is already owned by node '{remote.NodeId}' with handle {remote.Handle.Value}.");
					}

					_client.KeyExpire(serviceKey, expiry);
				}
			}

			var handleKey = HandleKey(handle.Value);
			var handleClaimed = _client.TryClaimKey(handleKey, _options.NodeId, expiry, out var handleOwner);
			if (!handleClaimed && handleOwner is not null && !string.Equals(handleOwner, _options.NodeId, StringComparison.Ordinal))
			{
				throw new InvalidOperationException($"Handle {handle.Value} is already associated with node '{handleOwner}'.");
			}

			_client.KeyExpire(handleKey, expiry);
			_localServices[name] = new LocalRegistration(handle, encodedLocation);
			CacheLocal(location, name);
			_client.Publish(GetEventChannel(), $"service|{name}|{location.NodeId}|{handle.Value}");
		}
	}

	/// <inheritdoc />
	public void UnregisterLocalActor(string name, ActorHandle handle)
	{
		ArgumentException.ThrowIfNullOrEmpty(name);
		if (!handle.IsValid)
		{
			return;
		}

		ThrowIfDisposed();

		lock (_localServicesLock)
		{
			_localServices.TryRemove(name, out _);
			_nameCache.TryRemove(name, out _);
			_handleCache.TryRemove(handle.Value, out _);
			_client.KeyDelete(ServiceKey(name));
			_client.KeyDelete(HandleKey(handle.Value));
			_client.Publish(GetEventChannel(), $"remove|{name}|{handle.Value}");
		}
	}

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_client.ConnectionRestored -= OnConnectionRestored;
		await _cts.CancelAsync();
		try
		{
			await _heartbeatTask.ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// Ignore cancellation during shutdown.
		}

		_subscription.Dispose();
		lock (_localServicesLock)
		{
			foreach (var pair in _localServices)
			{
				_client.KeyDelete(ServiceKey(pair.Key));
				_client.KeyDelete(HandleKey(pair.Value.Handle.Value));
			}
		}

		_client.KeyDelete(_nodeKey);
		await _client.DisposeAsync().ConfigureAwait(false);
		_cts.Dispose();
	}

	private void RegisterNode()
	{
		_client.SetString(_nodeKey, _encodedNodeEndpoint, _options.RegistrationTtl);
		CacheNode(_options.NodeId, new ClusterNodeDescriptor(_options.NodeId, _options.LocalEndPoint));
	}

	private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				await Task.Delay(_options.HeartbeatInterval, cancellationToken).ConfigureAwait(false);
				RefreshLocalEntries();
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Redis cluster registry heartbeat failed.");
			}
		}
	}

	private void RefreshLocalEntries()
	{
		var expiry = _options.RegistrationTtl;
		RefreshNodeEntry(expiry);
		lock (_localServicesLock)
		{
			foreach (var pair in _localServices)
			{
				try
				{
					RefreshServiceEntry(pair.Key, pair.Value, expiry);
				}
				catch (Exception ex)
				{
					// A transient failure on one entry (e.g. a dropped connection) must not abort the
					// refresh of the remaining entries; every entry is retried on the next heartbeat.
					_logger.LogWarning(ex, "Failed to refresh registry entry for service '{Name}'.", pair.Key);
				}
			}
		}
	}

	private void RefreshNodeEntry(TimeSpan expiry)
	{
		try
		{
			if (_client.KeyExpire(_nodeKey, expiry))
			{
				return;
			}

			// The node key is gone (TTL expired or evicted). Re-register from the value copy taken at startup.
			_client.SetString(_nodeKey, _encodedNodeEndpoint, expiry);
			_logger.LogInformation("Node entry '{NodeId}' was missing in Redis and has been re-registered (self-heal).", _options.NodeId);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to refresh node entry '{NodeId}'.", _options.NodeId);
		}
	}

	private void RefreshServiceEntry(string name, LocalRegistration registration, TimeSpan expiry)
	{
		var serviceKey = ServiceKey(name);
		if (!_client.KeyExpire(serviceKey, expiry))
		{
			SelfHealServiceKey(name, registration, serviceKey, expiry);
		}

		var handleKey = HandleKey(registration.Handle.Value);
		if (!_client.KeyExpire(handleKey, expiry))
		{
			SelfHealHandleKey(name, registration.Handle, handleKey, expiry);
		}
	}

	/// <summary>
	/// Re-registers a lost service key from the value copy captured at registration time. The claim is
	/// conditional (only written when the key is absent), so a racing writer that recreated the key first
	/// wins and its value is preserved instead of being overwritten (same guarded re-grant pattern as
	/// EtcdManager.RecoverLeaseAsync).
	/// </summary>
	private void SelfHealServiceKey(string name, LocalRegistration registration, string serviceKey, TimeSpan expiry)
	{
		if (_client.TryClaimKey(serviceKey, registration.EncodedLocation, expiry, out var owner))
		{
			_logger.LogInformation("Service '{Name}' key was lost in Redis and has been re-registered (self-heal).", name);
			return;
		}

		if (string.Equals(owner, registration.EncodedLocation, StringComparison.Ordinal))
		{
			// A racing self-heal or re-registration restored the identical value; only the TTL needs refreshing.
			_client.KeyExpire(serviceKey, expiry);
			return;
		}

		_logger.LogWarning("Service '{Name}' is now owned by '{Owner}' in Redis; skipping self-heal re-registration.", name, owner);
	}

	private void SelfHealHandleKey(string name, ActorHandle handle, string handleKey, TimeSpan expiry)
	{
		if (_client.TryClaimKey(handleKey, _options.NodeId, expiry, out var owner))
		{
			_logger.LogInformation("Handle {Handle} for service '{Name}' was lost in Redis and has been re-registered (self-heal).", handle.Value, name);
			return;
		}

		if (string.Equals(owner, _options.NodeId, StringComparison.Ordinal))
		{
			_client.KeyExpire(handleKey, expiry);
			return;
		}

		_logger.LogWarning("Handle {Handle} for service '{Name}' is now owned by node '{Owner}'; skipping self-heal re-registration.", handle.Value, name, owner);
	}

	private void OnConnectionRestored()
	{
		// StackExchange.Redis raises this on its socket worker thread; Redis I/O must never run there,
		// so reconciliation is pushed to the thread pool. Exceptions are contained inside the worker.
		Task.Run(ReconcileSubscription);
	}

	/// <summary>
	/// Full-scan diff reconciliation after a pub/sub disconnect: messages published while the subscription
	/// was down are lost, so the registry re-reads every service key and replays the difference against the
	/// last delivered state as synthetic events through the regular event handler. Keys whose content is
	/// unchanged are suppressed (no-op replay) to avoid churn in downstream consumers.
	/// </summary>
	private void ReconcileSubscription()
	{
		try
		{
			var prefix = $"{_options.KeyPrefix}:services:";
			// Normalize the scan result from full Redis keys to service names before diffing.
			var current = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var pair in _client.ReadByPrefix(prefix))
			{
				current[pair.Key[prefix.Length..]] = pair.Value;
			}

			var added = 0;
			var removed = 0;
			var suppressed = 0;

			// Removed: previously delivered to subscribers but no longer present in Redis.
			foreach (var name in _eventSnapshot.Keys.ToArray())
			{
				if (current.ContainsKey(name) || !_eventSnapshot.TryRemove(name, out var encoded))
				{
					continue;
				}

				if (TrySplitLocation(encoded, out var handleValue))
				{
					OnEventMessage($"remove|{name}|{handleValue}");
					removed++;
				}
			}

			// Added or changed: present in Redis but missing from (or different to) the last delivered state.
			foreach (var pair in current.OrderBy(static p => p.Key, StringComparer.Ordinal))
			{
				var name = pair.Key;
				if (_eventSnapshot.TryGetValue(name, out var seen) && string.Equals(seen, pair.Value, StringComparison.Ordinal))
				{
					// The replayed content matches what subscribers already received; suppress the no-op event.
					suppressed++;
					continue;
				}

				_eventSnapshot[name] = pair.Value;
				OnEventMessage($"service|{name}|{pair.Value}");
				added++;
			}

			_logger.LogInformation("Registry reconciliation after reconnect replayed {Added} added, {Removed} removed, {Suppressed} suppressed.", added, removed, suppressed);
		}
		catch (Exception ex)
		{
			// The reconciliation read itself failed (still disconnected or Redis unavailable); the next
			// ConnectionRestored trigger retries. Never let this kill the background worker silently.
			_logger.LogWarning(ex, "Registry reconciliation after reconnect failed.");
		}
	}

	private void CacheLookup(string name, ClusterActorLocation location)
	{
		var expiry = CalculateCacheExpiry();
		var entry = new CacheEntry(location, expiry);
		_nameCache[name] = entry;
		_handleCache[location.Handle.Value] = entry;
	}

	private void CacheHandle(long handle, ClusterActorLocation location)
	{
		var expiry = CalculateCacheExpiry();
		_handleCache[handle] = new CacheEntry(location, expiry);
	}

	private void CacheLocal(ClusterActorLocation location, string name)
	{
		var entry = new CacheEntry(location, DateTimeOffset.MaxValue);
		_nameCache[name] = entry;
		_handleCache[location.Handle.Value] = entry;
		CacheNode(_options.NodeId, new ClusterNodeDescriptor(_options.NodeId, _options.LocalEndPoint));
	}

	private void CacheNode(string nodeId, ClusterNodeDescriptor descriptor)
	{
		var expiry = nodeId == _options.NodeId ? DateTimeOffset.MaxValue : CalculateCacheExpiry();
		_nodeCache[nodeId] = new NodeCacheEntry(descriptor, expiry);
	}

	private bool TryGetCachedLocation(string name, out ClusterActorLocation location)
	{
		if (_nameCache.TryGetValue(name, out var entry) && entry.IsValid)
		{
			location = entry.Location;
			return true;
		}

		location = null!;
		return false;
	}

	private bool TryGetCachedHandle(long handle, out ClusterActorLocation location)
	{
		if (_handleCache.TryGetValue(handle, out var entry) && entry.IsValid)
		{
			location = entry.Location;
			return true;
		}

		location = null!;
		return false;
	}

	private bool TryGetCachedNode(string nodeId, out ClusterNodeDescriptor descriptor)
	{
		if (_nodeCache.TryGetValue(nodeId, out var entry) && entry.IsValid)
		{
			descriptor = entry.Descriptor;
			return true;
		}

		descriptor = null!;
		return false;
	}

	private void EnsureNodeCached(string nodeId)
	{
		if (TryGetCachedNode(nodeId, out _))
		{
			return;
		}

		var value = _client.GetString(NodeKey(nodeId));
		if (string.IsNullOrEmpty(value))
		{
			return;
		}

		if (!TryParseEndpoint(value, out var endpoint))
		{
			return;
		}

		CacheNode(nodeId, new ClusterNodeDescriptor(nodeId, endpoint));
	}

	private void OnEventMessage(string message)
	{
		if (string.IsNullOrWhiteSpace(message))
		{
			return;
		}

		var parts = message.Split('|', StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length == 0)
		{
			return;
		}

		switch (parts[0])
		{
			case "service" when parts.Length == 4:
			{
				var name = parts[1];
				var nodeId = parts[2];
				if (!long.TryParse(parts[3], out var handleValue))
				{
					return;
				}

				var location = new ClusterActorLocation(nodeId, new ActorHandle(handleValue));
				_eventSnapshot[name] = EncodeLocation(location);
				CacheLookup(name, location);
				EnsureNodeCached(nodeId);
				break;
			}
			case "remove" when parts.Length >= 3:
			{
				var name = parts[1];
				_eventSnapshot.TryRemove(name, out _);
				_nameCache.TryRemove(name, out _);
				if (long.TryParse(parts[2], out var handleValue))
				{
					_handleCache.TryRemove(handleValue, out _);
				}

				break;
			}
		}
	}

	private static string EncodeLocation(ClusterActorLocation location)
	{
		return $"{location.NodeId}|{location.Handle.Value}";
	}

	private static ClusterActorLocation DecodeLocation(string value)
	{
		var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length != 2 || !long.TryParse(parts[1], out var handle))
		{
			throw new FormatException("Invalid location payload encountered in Redis registry.");
		}

		return new ClusterActorLocation(parts[0], new ActorHandle(handle));
	}

	private static string EncodeEndpoint(IPEndPoint endpoint)
	{
		return $"{endpoint.Address}|{endpoint.Port}";
	}

	private static bool TryParseEndpoint(string value, out IPEndPoint endpoint)
	{
		var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length != 2 || !int.TryParse(parts[1], out var port))
		{
			endpoint = null!;
			return false;
		}

		if (!IPAddress.TryParse(parts[0], out var address))
		{
			endpoint = null!;
			return false;
		}

		endpoint = new IPEndPoint(address, port);
		return true;
	}

	private DateTimeOffset CalculateCacheExpiry()
	{
		return _options.CacheTtl <= TimeSpan.Zero ? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow.Add(_options.CacheTtl);
	}

	private string ServiceKey(string name) => $"{_options.KeyPrefix}:services:{name}";

	private string HandleKey(long handle) => $"{_options.KeyPrefix}:handles:{handle}";

	private string NodeKey(string nodeId) => $"{_options.KeyPrefix}:nodes:{nodeId}";

	private string GetEventChannel() => $"{_options.KeyPrefix}:events";

	private void ThrowIfDisposed()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
	}

	private void ValidateOptions(RedisClusterRegistryOptions options)
	{
		ArgumentException.ThrowIfNullOrEmpty(options.NodeId);
		if (options.LocalEndPoint is null)
		{
			throw new ArgumentException("Local endpoint must be specified.", nameof(options));
		}

		if (options.LocalEndPoint.Port <= 0 || options.LocalEndPoint.Port > 65535)
		{
			throw new ArgumentOutOfRangeException(nameof(options), options.LocalEndPoint.Port, "Endpoint port must be between 1 and 65535.");
		}

		if (options.RegistrationTtl <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(options), "Registration TTL must be positive.");
		}

		if (options.HeartbeatInterval <= TimeSpan.Zero || options.HeartbeatInterval >= options.RegistrationTtl)
		{
			throw new ArgumentException("Heartbeat interval must be positive and shorter than the registration TTL.", nameof(options));
		}
	}

	private static bool TrySplitLocation(string encoded, out long handleValue)
	{
		handleValue = 0;
		var parts = encoded.Split('|', StringSplitOptions.RemoveEmptyEntries);
		return parts.Length == 2 && long.TryParse(parts[1], out handleValue);
	}

	private readonly record struct CacheEntry(ClusterActorLocation Location, DateTimeOffset ExpiresAt)
	{
		public bool IsValid => ExpiresAt == DateTimeOffset.MaxValue || ExpiresAt > DateTimeOffset.UtcNow;
	}

	private readonly record struct NodeCacheEntry(ClusterNodeDescriptor Descriptor, DateTimeOffset ExpiresAt)
	{
		public bool IsValid => ExpiresAt == DateTimeOffset.MaxValue || ExpiresAt > DateTimeOffset.UtcNow;
	}

	/// <summary>
	/// A local registration and the encoded value copy kept for self-heal re-registration when the
	/// corresponding Redis keys are lost.
	/// </summary>
	private sealed record LocalRegistration(ActorHandle Handle, string EncodedLocation);
}

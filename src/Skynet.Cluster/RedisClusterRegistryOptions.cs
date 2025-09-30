using System;
using System.Net;

namespace Skynet.Cluster;

/// <summary>
/// Provides configuration for the <see cref="RedisClusterRegistry"/>.
/// </summary>
public sealed class RedisClusterRegistryOptions
{
	/// <summary>
	/// Gets or sets the Redis connection string.
	/// </summary>
	public string ConnectionString { get; init; } = "localhost";

	/// <summary>
	/// Gets or sets the logical database index used by the registry.
	/// </summary>
	public int Database { get; init; }

	/// <summary>
	/// Gets or sets the prefix applied to all Redis keys.
	/// </summary>
	public string KeyPrefix { get; init; } = "skynet:cluster";

	/// <summary>
	/// Gets or sets the identifier of the current node.
	/// </summary>
	public string NodeId { get; init; } = string.Empty;

	/// <summary>
	/// Gets or sets the TCP endpoint exposed by the current node.
	/// </summary>
	public IPEndPoint LocalEndPoint { get; init; } = new(IPAddress.Loopback, 0);

	/// <summary>
	/// Gets or sets the time-to-live applied to registry entries.
	/// </summary>
	public TimeSpan RegistrationTtl { get; init; } = TimeSpan.FromSeconds(15);

	/// <summary>
	/// Gets or sets the interval at which the registry refreshes its TTLs.
	/// </summary>
	public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);

	/// <summary>
	/// Gets or sets the lifespan of cached lookups stored locally.
	/// </summary>
	public TimeSpan CacheTtl { get; init; } = TimeSpan.FromSeconds(1);
}

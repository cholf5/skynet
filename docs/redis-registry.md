# Redis Cluster Registry Plugin

The Redis-backed cluster registry enables dynamic service discovery, uniqueservice coordination, and node heartbeat tracking across Skynet nodes. It complements the built-in `StaticClusterRegistry` by persisting service metadata in Redis so that nodes can join or leave at runtime without modifying static configuration.

## Features

- **Dynamic node discovery** – each node registers its `NodeId` and transport endpoint under a configurable prefix. Heartbeats extend the TTL to keep entries fresh and automatically remove crashed nodes.
- **Named service coordination** – `RegisterLocalActor` uses `SETNX` semantics so only one node can claim a named service. Attempts to register a duplicate throw an exception, mirroring Skynet's uniqueservice semantics.
- **Handle resolution** – actor handles are mapped to their owning node, allowing transports to resolve remote targets via Redis lookups.
- **Pub/Sub cache invalidation** – registry updates publish notifications so other nodes can refresh their caches immediately when services register or unregister.

## Configuration

Create the registry by supplying `RedisClusterRegistryOptions` when bootstrapping the `ActorSystem`:

```csharp
var registryOptions = new RedisClusterRegistryOptions
{
ConnectionString = "localhost:6379",
NodeId = "node-a",
LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 3601),
RegistrationTtl = TimeSpan.FromSeconds(10),
HeartbeatInterval = TimeSpan.FromSeconds(3),
CacheTtl = TimeSpan.FromMilliseconds(250)
};

await using var registry = new RedisClusterRegistry(registryOptions);
await using var system = new ActorSystem(
loggerFactory,
transport: null,
options: new ActorSystemOptions { ClusterRegistry = registry },
transportFactory: sys => new TcpTransport(sys, registry));
```

> **Note:** when the registry is owned by the actor system, dispose it after shutting down the system to remove node keys eagerly. The actor system now disposes registries that implement `IAsyncDisposable` or `IDisposable` during shutdown.

### Options

| Option | Description |
| --- | --- |
| `ConnectionString` | Redis connection string used by the default StackExchange client. |
| `NodeId` | Unique identifier for the local node. |
| `LocalEndPoint` | External endpoint advertised to other nodes. |
| `RegistrationTtl` | TTL applied to node, service, and handle entries. |
| `HeartbeatInterval` | Interval for refreshing TTLs; must be shorter than the TTL. |
| `CacheTtl` | Local in-memory cache duration for lookups. |
| `KeyPrefix` | Optional key namespace (defaults to `skynet:cluster`). |
| `Database` | Logical Redis database index (default `0`). |

## Failure Handling

- If a node stops heartbeating, Redis evicts its entries when the TTL expires, and subscribers drop the stale cache entries.
- Duplicate service registrations throw `InvalidOperationException` so callers can retry against the owning node.
- `UnregisterLocalActor` removes keys immediately when an actor is shut down, preventing the registry from returning stale handles during graceful shutdown.

## Testing Without Redis

The test suite includes a lightweight in-memory `FakeRedisServer` that implements the required Redis primitives (`SETNX`, `EXPIRE`, pub/sub) so you can validate registry behaviour without a real Redis instance. See `RedisClusterRegistryTests` for examples of registering, resolving, and invalidating services.

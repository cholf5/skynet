# Placement Strategies

PRD 10.3 defines three ways to decide which cluster node hosts an actor. Skynet ships
`ConsistentHashPlacement` (`Skynet.Cluster`) as an *optional* placement helper; the default
remains **direct** resolution through `IClusterRegistry`. Placement is deliberately not wired
into the registry main path — `IPlacementStrategy` is a standalone, composable building block.

## The three strategies

| Strategy | Decision | Use when |
| --- | --- | --- |
| **Direct** (default) | Caller already knows the target (`ActorHandle` or service name) and the registry resolves it to a node. | Most services: login, datacenter, gate routing — anything with a fixed owner or a unique service. |
| **Manual** | The application picks the node explicitly and creates the actor there (e.g. via a control channel or node-scoped service name). | Sharded actors that need explicit placement control, or capacity-managed placement (each node hosts a bounded number of rooms). |
| **ConsistentHash** (`ConsistentHashPlacement`) | A stable key (e.g. `room:<id>`) is hashed onto a consistent hash ring of participating nodes; the ring owner hosts the actor. | Large numbers of small, stateful, key-addressable actors (rooms, channels, per-entity caches) that should spread evenly and survive node topology changes with minimal migration. |

### When *not* to use consistent hashing

- **Few, fixed nodes with a handful of well-known services** — direct/manual is simpler and
  needs no ring maintenance.
- **Strict single-owner guarantees** — the ring is a *routing hint*, not an ownership lease.
  During a topology change the old and new owner can both believe they host a key for a short
  window. If you need mutual exclusion per name, coordinate through the registry's unique
  service semantics (e.g. `RedisClusterRegistry.RegisterLocalActor`) or an application-level
  lease on top of the ring decision.
- **Weighted or latency-aware placement** — the ring distributes by hash position only; add
  capacity awareness above it if needed.

## API

```csharp
IPlacementStrategy placement = new ConsistentHashPlacement(
    ["node-a", "node-b", "node-c", "node-d"],
    virtualNodeCount: ConsistentHashPlacement.DefaultVirtualNodeCount); // 160

// Room 42 always maps to the same node while the node set is unchanged.
var targetNode = placement.Place("room:42");

// Nodes join/leave: replace the participating set (atomic, thread-safe rebuild).
placement.UpdateNodes(["node-a", "node-b", "node-c"]); // node-d left

// The current participating set (sorted, ordinal).
foreach (var node in placement.Nodes)
{
    Console.WriteLine(node);
}
```

Combining placement with the registry: `Place` yields a node id, and the registry tells you
the endpoint / how to reach it. Creating the actor on the target node is the application's
responsibility (e.g. send a spawn request to a per-node supervisor service).

## Semantics and guarantees

- **Stability (stickiness)** — the same key maps to the same node as long as the node set is
  unchanged, independent of process, enumeration order, or instance.
- **Minimal migration** — removing 1/N of the nodes migrates ~1/N of the keys and leaves every
  other key on its node. Adding one node to N existing ones reassigns ~1/(N+1) of the keys.
  Measured with 4 nodes / 160 virtual nodes / 4000 keys: removing 1 node migrated **23.92%**
  (expected 25%), adding a 5th node migrated **18.50%** (expected 20%).
- **Empty node sets are rejected eagerly** — the constructor and `UpdateNodes` throw
  `ArgumentException` for an empty (or invalid) set. A placement strategy without participants
  is a configuration error, not a per-lookup failure; consequently `Place` is total and always
  returns a node from the current snapshot. A failed update never corrupts the current ring.
- **Duplicates** — repeated node ids are collapsed (set semantics); `null`/empty ids are
  rejected.
- **Determinism across nodes** — every process that derives the ring from the same node ids
  with the same `virtualNodeCount` produces an identical ring. **All nodes must use the same
  virtual node count**; different counts produce different rings and split-brain placement.
- **Hash** — FNV-1a 64 followed by a murmur3 `fmix64` finalizer (stable, non-cryptographic;
  same FNV family as payload contract ids). The finalizer is required: raw FNV-1a clusters
  structured short strings (`room:123`, `node-3#42`) onto a few ring segments (measured: one
  node owning >50% of keys with uniform keys otherwise).
- **Concurrency** — lookups are lock-free. The ring is an immutable snapshot swapped by a
  `Volatile` write, so `UpdateNodes` while readers call `Place` is safe: a reader sees either
  the old or the new ring in full, never a partial one. Concurrent `UpdateNodes` calls are
  atomic (last complete set wins).

## Choosing the virtual node count

Each node contributes `virtualNodeCount` ring positions. More virtual nodes → smoother share
per node, at the cost of a larger (but still tiny) sorted array.

| Count | Effect |
| --- | --- |
| `1` | Valid but statistically meaningless — one hash position decides the node's whole share; expect large skew. |
| `100–200` (default 160) | Per-node share deviation of a few percent for small-to-medium clusters; recommended. |
| `> 10000` | Rejected (`ArgumentOutOfRangeException`); ring rebuild cost grows without practical benefit. |

The valid range is 1..10000. Values outside it throw `ArgumentOutOfRangeException`.

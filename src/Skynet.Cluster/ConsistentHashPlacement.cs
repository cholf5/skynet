using System.Buffers;
using System.Text;

namespace Skynet.Cluster;

/// <summary>
/// Places keys onto nodes with a consistent hash ring: each node is hashed onto the ring
/// through multiple virtual nodes, and a key is owned by the first ring entry at or after
/// the key's own hash position.
/// </summary>
/// <remarks>
/// <para>
/// The mapping only depends on the participating node set, so every process that feeds the
/// same node ids (ordinal, deduplicated) with the same virtual node count derives an
/// identical ring without coordination. When the node set changes, only the ring segments
/// that belonged to added or removed nodes change hands: removing 1/N of the nodes migrates
/// approximately 1/N of the keys and all other keys stay on their node.
/// </para>
/// <para>
/// Ring positions are derived with FNV-1a 64 followed by a murmur3 fmix64 finalizer — a
/// stable, non-cryptographic combination (the same FNV family the payload contract ids use).
/// The finalizer is required because raw FNV-1a clusters structured short strings
/// ("room:123", "node-3#42") onto a few ring segments, which would skew node ownership.
/// </para>
/// <para>
/// Lookups are lock-free: the ring is an immutable snapshot that is swapped atomically
/// (<see cref="Volatile"/>) by <see cref="UpdateNodes"/>, so readers either see the old or
/// the new ring in full. The empty node set is rejected eagerly at construction and update
/// time — a placement strategy without participants is treated as a configuration error
/// rather than a per-lookup failure, which keeps <see cref="Place"/> total (it always
/// returns a node from the current snapshot).
/// </para>
/// </remarks>
public sealed class ConsistentHashPlacement : IPlacementStrategy
{
	/// <summary>
	/// The default number of virtual nodes each physical node contributes to the ring.
	/// </summary>
	/// <remarks>
	/// 160 keeps the per-node ring share deviation around a few percent for small clusters
	/// while the sorted ring stays cheap to rebuild.
	/// </remarks>
	public const int DefaultVirtualNodeCount = 160;

	private const int MinVirtualNodeCount = 1;
	private const int MaxVirtualNodeCount = 10000;

	// FNV-1a 64-bit constants (stable, non-cryptographic; same family as the FNV-1a 32
	// contract id hash in Skynet.Core.Serialization.PayloadContractRegistry).
	private const ulong Fnv1A64OffsetBasis = 14695981039346656037;
	private const ulong Fnv1A64Prime = 1099511628211;

	// murmur3 fmix64 finalizer constants. FNV-1a alone has weak avalanche on structured short
	// strings (e.g. "node-3#42", "room:123"), which clusters both ring positions and key
	// positions onto a few ring segments; the finalizer spreads them uniformly again.
	private const ulong MixConstant1 = 0xff51afd7ed558ccd;
	private const ulong MixConstant2 = 0xc4ceb9fe1a85ec53;

	// Above this size a key's UTF-8 encoding no longer fits the stack buffer and is hashed
	// through a rented buffer instead.
	private const int StackallocByteThreshold = 256;

	private readonly int _virtualNodeCount;
	private Ring _ring;

	/// <summary>
	/// Initializes a new instance of the <see cref="ConsistentHashPlacement"/> class.
	/// </summary>
	/// <param name="nodeIds">The initial participating nodes; must contain at least one distinct node id.</param>
	/// <param name="virtualNodeCount">The number of virtual ring positions each node contributes.</param>
	public ConsistentHashPlacement(IEnumerable<string> nodeIds, int virtualNodeCount = DefaultVirtualNodeCount)
	{
		ArgumentNullException.ThrowIfNull(nodeIds);
		_virtualNodeCount = ValidateVirtualNodeCount(virtualNodeCount);
		_ring = BuildRing(nodeIds, _virtualNodeCount);
	}

	/// <inheritdoc />
	public IReadOnlyList<string> Nodes => Volatile.Read(ref _ring).NodeIds;

	/// <inheritdoc />
	public string Place(string key)
	{
		ArgumentException.ThrowIfNullOrEmpty(key);
		var ring = Volatile.Read(ref _ring);
		var entries = ring.Entries;
		var hash = Fnv1A64(key);
		var index = Array.BinarySearch(entries, new RingEntry(hash, string.Empty));
		if (index < 0)
		{
			index = ~index;
		}

		return index < entries.Length ? entries[index].NodeId : entries[0].NodeId;
	}

	/// <inheritdoc />
	public void UpdateNodes(IReadOnlyCollection<string> nodeIds)
	{
		ArgumentNullException.ThrowIfNull(nodeIds);
		// Build the immutable snapshot first and only then swap the reference: a rejected
		// update (invalid ids, empty set) must leave the current ring untouched, and a
		// successful one becomes visible atomically.
		var ring = BuildRing(nodeIds, _virtualNodeCount);
		Volatile.Write(ref _ring, ring);
	}

	private static int ValidateVirtualNodeCount(int virtualNodeCount)
	{
		if (virtualNodeCount < MinVirtualNodeCount || virtualNodeCount > MaxVirtualNodeCount)
		{
			throw new ArgumentOutOfRangeException(
				nameof(virtualNodeCount),
				virtualNodeCount,
				$"Virtual node count must be between {MinVirtualNodeCount} and {MaxVirtualNodeCount}.");
		}

		return virtualNodeCount;
	}

	private static Ring BuildRing(IEnumerable<string> nodeIds, int virtualNodeCount)
	{
		// The caller validated the collection reference and non-emptiness before building.
		var distinct = new HashSet<string>(StringComparer.Ordinal);
		foreach (var nodeId in nodeIds)
		{
			if (string.IsNullOrEmpty(nodeId))
			{
				throw new ArgumentException("Node ids must not be null or empty.", nameof(nodeIds));
			}

			distinct.Add(nodeId);
		}

		if (distinct.Count == 0)
		{
			throw new ArgumentException(
				"At least one node must participate in placement.", nameof(nodeIds));
		}

		// Sort before generating virtual nodes so the ring contents (and the owner picked
		// between equal hash positions) do not depend on the enumeration order of nodeIds.
		var nodes = distinct.ToArray();
		Array.Sort(nodes, StringComparer.Ordinal);

		var entries = new RingEntry[nodes.Length * virtualNodeCount];
		var entryIndex = 0;
		foreach (var nodeId in nodes)
		{
			for (var replica = 0; replica < virtualNodeCount; replica++)
			{
				entries[entryIndex++] = new RingEntry(Fnv1A64($"{nodeId}#{replica}"), nodeId);
			}
		}

		Array.Sort(entries);
		return new Ring(entries, nodes);
	}

	private static ulong Fnv1A64(string value)
	{
		var maxBytes = Encoding.UTF8.GetMaxByteCount(value.Length);
		byte[]? rented = null;
		Span<byte> buffer = maxBytes <= StackallocByteThreshold
			? stackalloc byte[StackallocByteThreshold]
			: rented = ArrayPool<byte>.Shared.Rent(maxBytes);
		try
		{
			var written = Encoding.UTF8.GetBytes(value, buffer);
			var hash = Fnv1A64OffsetBasis;
			foreach (var byteValue in buffer[..written])
			{
				hash ^= byteValue;
				hash *= Fnv1A64Prime;
			}

			// Finalize for uniform avalanche; without it structured keys and virtual node labels
			// concentrate on a handful of ring segments (measured: one node owning >50% of keys).
			hash ^= hash >> 33;
			hash *= MixConstant1;
			hash ^= hash >> 33;
			hash *= MixConstant2;
			hash ^= hash >> 33;
			return hash;
		}
		finally
		{
			if (rented is not null)
			{
				ArrayPool<byte>.Shared.Return(rented);
			}
		}
	}

	/// <summary>
	/// An immutable consistent hash ring: virtual node entries sorted by ring position and a
	/// sorted copy of the participating node ids.
	/// </summary>
	private sealed class Ring(RingEntry[] entries, string[] nodeIds)
	{
		public RingEntry[] Entries { get; } = entries;

		public string[] NodeIds { get; } = nodeIds;
	}

	/// <summary>
	/// A single virtual node on the ring, ordered by hash position and node id so that
	/// equal-position collisions resolve deterministically on every process.
	/// </summary>
	private readonly record struct RingEntry(ulong Hash, string NodeId) : IComparable<RingEntry>
	{
		public int CompareTo(RingEntry other)
		{
			var hashComparison = Hash.CompareTo(other.Hash);
			return hashComparison != 0
				? hashComparison
				: string.CompareOrdinal(NodeId, other.NodeId);
		}
	}
}

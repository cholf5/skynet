using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Skynet.Core.Serialization;

/// <summary>
/// Runtime registry that maps stable payload contract ids to payload types.
/// <para>
/// The wire protocol only carries an <c>int</c> contract id inside an envelope; payload types are
/// resolved through this table instead of runtime string-to-type reflection. Ids are computed from
/// the payload type's <see cref="Type.FullName"/> with a stable FNV-1a 32-bit hash so every node
/// derives identical ids for the same type regardless of compilation or load order. Types compiled
/// with <c>[SkynetActor]</c> contracts are registered at module initialization time by generated
/// code; any other payload type self-regulates on the sending side the first time it is serialized.
/// The receiving side never invents types: resolving an unknown id throws
/// <see cref="UnknownPayloadContractException"/>.
/// </para>
/// </summary>
public static class PayloadContractRegistry
{
	/// <summary>
	/// Reserved contract id used on the wire for a <see langword="null"/> payload. Real types never
	/// receive this id; <see cref="ComputeContractId"/> re-hashes deterministically on collision.
	/// </summary>
	public const int NullPayloadContractId = 0;

	private static readonly ConcurrentDictionary<int, Type> TypesById = new();
	private static readonly ConcurrentDictionary<Type, int> ContractIdsByType = new();
	private static readonly Lock SyncRoot = new();

	static PayloadContractRegistry()
	{
		// Pre-register the primitive payload types that commonly appear as responses or plain
		// messages (e.g. string return values). Their ids are deterministic on every node.
		Register<string>();
		Register<bool>();
		Register<char>();
		Register<byte>();
		Register<sbyte>();
		Register<short>();
		Register<ushort>();
		Register<int>();
		Register<uint>();
		Register<long>();
		Register<ulong>();
		Register<float>();
		Register<double>();
		Register<decimal>();
		Register<DateTime>();
		Register<DateTimeOffset>();
		Register<TimeSpan>();
		Register<Guid>();
		Register<byte[]>();
		Register<global::Skynet.Core.RpcMessages.EmptyPayload>();
	}

	/// <summary>
	/// Registers a payload type under its computed contract id. Registering the same type (or the
	/// same full name) again is idempotent; registering a different full name under an id that is
	/// already taken throws.
	/// </summary>
	public static void Register<TPayload>()
	{
		Register(typeof(TPayload));
	}

	/// <inheritdoc cref="Register{TPayload}()" />
	public static void Register(Type payloadType)
	{
		RegisterCore(payloadType, contractIdOverride: null);
	}

	/// <summary>
	/// Registers a payload type under an explicit contract id. Intended for tests that need to
	/// simulate hash collisions; production code must use the computed id.
	/// </summary>
	internal static void Register(Type payloadType, int contractId)
	{
		RegisterCore(payloadType, contractId);
	}

	/// <summary>
	/// Returns the contract id for the payload type, registering the type on first use. This is the
	/// sending-side self-registration path: the sender already knows the concrete type, so no
	/// reflection is required and the receiving node resolves the id through its own table.
	/// </summary>
	public static int GetOrRegister(Type payloadType)
	{
		ArgumentNullException.ThrowIfNull(payloadType);
		if (ContractIdsByType.TryGetValue(payloadType, out var existing))
		{
			return existing;
		}

		RegisterCore(payloadType, contractIdOverride: null);
		return ContractIdsByType[payloadType];
	}

	/// <summary>
	/// Resolves a contract id received from the wire to its registered payload type. This is the
	/// only payload-type lookup performed when deserializing envelopes.
	/// </summary>
	public static bool TryResolve(int contractId, [NotNullWhen(true)] out Type? payloadType)
	{
		if (contractId == NullPayloadContractId)
		{
			payloadType = null;
			return false;
		}

		return TypesById.TryGetValue(contractId, out payloadType);
	}

	/// <summary>
	/// Attempts to look up the contract id of an already registered payload type without
	/// registering it.
	/// </summary>
	public static bool TryGetContractId(Type payloadType, out int contractId)
	{
		ArgumentNullException.ThrowIfNull(payloadType);
		return ContractIdsByType.TryGetValue(payloadType, out contractId);
	}

	/// <summary>
	/// Computes the stable contract id for a payload type full name: FNV-1a 32-bit over the UTF-8
	/// bytes of the name. The result depends only on the string, so two nodes that share contract
	/// definitions agree on ids even if they compile or load types in different orders.
	/// </summary>
	public static int ComputeContractId(string payloadTypeFullName)
	{
		ArgumentException.ThrowIfNullOrEmpty(payloadTypeFullName);
		var id = Fnv1a32(Encoding.UTF8.GetBytes(payloadTypeFullName));
		// Contract id 0 is reserved for null payloads. The collision probability is ~2^-32 per
		// type; when it happens, re-hash deterministically so all nodes derive the same id.
		for (var salt = 1; id == NullPayloadContractId; salt++)
		{
			id = Fnv1a32(Encoding.UTF8.GetBytes(payloadTypeFullName + "#" + salt.ToString(CultureInfo.InvariantCulture)));
		}

		return id;
	}

	private static void RegisterCore(Type payloadType, int? contractIdOverride)
	{
		ArgumentNullException.ThrowIfNull(payloadType);
		var fullName = payloadType.FullName ??
			throw new ArgumentException(
				$"Payload type '{payloadType}' has no full name (e.g. open generic or byref types are not supported).",
				nameof(payloadType));
		var contractId = contractIdOverride ?? ComputeContractId(fullName);

		lock (SyncRoot)
		{
			if (TypesById.TryGetValue(contractId, out var existing))
			{
				if (existing == payloadType ||
					string.Equals(existing.FullName, fullName, StringComparison.Ordinal))
				{
					// Same type (or same full name, e.g. side-by-side assembly loads): idempotent.
					ContractIdsByType[payloadType] = contractId;
					return;
				}

				throw new InvalidOperationException(
					$"Payload contract id {contractId} is already registered for type '{existing.FullName}' " +
					$"and cannot be reused by '{fullName}'. " +
					"Contract ids are derived from type full names; rename one of the types to resolve the conflict.");
			}

			if (ContractIdsByType.TryGetValue(payloadType, out var alreadyRegistered) && alreadyRegistered != contractId)
			{
				throw new InvalidOperationException(
					$"Payload type '{fullName}' is already registered under contract id {alreadyRegistered} " +
					$"and cannot be re-registered under id {contractId}.");
			}

			TypesById[contractId] = payloadType;
			ContractIdsByType[payloadType] = contractId;
		}
	}

	private static int Fnv1a32(byte[] data)
	{
		const uint offsetBasis = 2166136261;
		const uint prime = 16777619;
		uint hash = offsetBasis;
		foreach (var value in data)
		{
			hash ^= value;
			hash *= prime;
		}

		return unchecked((int)hash);
	}
}

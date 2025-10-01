using System.Collections.Concurrent;
using MessagePack;

namespace Skynet.Core;

public static class RpcContractRegistry
{
	private static readonly ConcurrentDictionary<Type, object> Dispatchers = new();
	private static readonly ConcurrentDictionary<Type, Func<ActorRef, MessagePackSerializerOptions?, object>> ProxyFactories = new();
	private static readonly ConcurrentDictionary<Type, (string? ServiceName, bool Unique)> Metadata = new();
	private static readonly Lock SyncRoot = new();

	public static void Register<TContract>(IRpcDispatcher<TContract> dispatcher, Func<ActorRef, MessagePackSerializerOptions?, TContract> proxyFactory, string? serviceName, bool unique)
		where TContract : class
	{
		ArgumentNullException.ThrowIfNull(dispatcher);
		ArgumentNullException.ThrowIfNull(proxyFactory);

		var contractType = typeof(TContract);
		lock (SyncRoot)
		{
			if (Dispatchers.TryGetValue(contractType, out var existing) && existing is IRpcDispatcher<TContract> registered)
			{
				if (registered.GetType() != dispatcher.GetType())
				{
					throw new InvalidOperationException($"RPC dispatcher for contract '{contractType.FullName}' is already registered with type {registered.GetType().FullName}.");
				}
			}
			else
			{
				Dispatchers[contractType] = dispatcher;
			}

			ProxyFactories[contractType] = proxyFactory;
			Metadata[contractType] = (serviceName, unique);
		}
	}

	public static IRpcDispatcher<TContract> GetDispatcher<TContract>() where TContract : class
	{
		if (Dispatchers.TryGetValue(typeof(TContract), out var dispatcher))
		{
			return (IRpcDispatcher<TContract>)dispatcher;
		}

		throw new InvalidOperationException($"No RPC dispatcher registered for contract '{typeof(TContract).FullName}'. Ensure the interface is annotated with [SkynetActor].");
	}

	public static TContract CreateProxy<TContract>(ActorRef actor, MessagePackSerializerOptions? options = null) where TContract : class
	{
		ArgumentNullException.ThrowIfNull(actor);
		if (ProxyFactories.TryGetValue(typeof(TContract), out var factory))
		{
			return (TContract)factory(actor, options);
		}

		throw new InvalidOperationException($"No RPC proxy factory registered for contract '{typeof(TContract).FullName}'. Ensure the interface is annotated with [SkynetActor].");
	}

	public static (string? ServiceName, bool Unique) GetMetadata<TContract>() where TContract : class
	{
		return Metadata.TryGetValue(typeof(TContract), out var meta) ? meta : (null, false);
	}
}

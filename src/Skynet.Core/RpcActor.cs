using System;
using System.Threading;
using System.Threading.Tasks;

namespace Skynet.Core;

public abstract class RpcActor<TContract> : Actor where TContract : class
	{
	private readonly IRpcDispatcher<TContract> _dispatcher;

	protected RpcActor()
	{
	if (this is not TContract)
	{
	throw new InvalidOperationException($"{GetType().FullName} must implement {typeof(TContract).FullName} to be used with RpcActor.");
	}

	_dispatcher = RpcContractRegistry.GetDispatcher<TContract>();
	}

	protected override async Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
	{
	try
	{
	return await _dispatcher.DispatchAsync((TContract)(object)this, envelope, cancellationToken).ConfigureAwait(false);
	}
	catch (RpcDispatchException)
	{
	return await OnUnhandledMessageAsync(envelope, cancellationToken).ConfigureAwait(false);
	}
	}

	protected virtual Task<object?> OnUnhandledMessageAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
	{
	throw new InvalidOperationException($"Unhandled payload '{envelope.Payload?.GetType().FullName}' for actor {GetType().FullName}.");
	}
	}

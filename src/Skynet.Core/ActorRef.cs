using System.Threading.Tasks;
using MessagePack;
namespace Skynet.Core;

/// <summary>
/// Provides a lightweight reference to an actor hosted inside an <see cref="ActorSystem"/>.
/// </summary>
public sealed class ActorRef
{
	internal ActorRef(ActorSystem system, ActorHandle handle)
	{
		System = system;
		Handle = handle;
	}

	/// <summary>
	/// Gets the <see cref="ActorHandle"/> represented by this reference.
	/// </summary>
	public ActorHandle Handle { get; }

	internal ActorSystem System { get; }

	/// <summary>
	/// Sends a fire-and-forget message to the actor.
	/// </summary>
	/// <param name="payload">The payload to deliver.</param>
	/// <param name="cancellationToken">Token used to cancel the send operation.</param>
	public ValueTask SendAsync(object payload, CancellationToken cancellationToken = default)
	{
		return System.SendAsync(Handle, payload, cancellationToken: cancellationToken);
	}

	/// <summary>
	/// Performs a request-response call to the actor.
	/// </summary>
	/// <typeparam name="TResponse">Type of the response payload.</typeparam>
	/// <param name="payload">The payload to deliver.</param>
	/// <param name="timeout">Optional timeout for the call.</param>
	/// <param name="cancellationToken">Token used to cancel the operation.</param>
	public Task<TResponse> CallAsync<TResponse>(object payload, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
	{
	return System.CallAsync<TResponse>(Handle, payload, timeout, cancellationToken: cancellationToken);
	}

	public TContract CreateProxy<TContract>(MessagePackSerializerOptions? options = null) where TContract : class
	{
	return RpcContractRegistry.CreateProxy<TContract>(this, options);
	}
	}

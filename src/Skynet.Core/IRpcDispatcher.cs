namespace Skynet.Core;

public interface IRpcDispatcher<in TContract> where TContract : class
	{
	Task<object?> DispatchAsync(TContract target, MessageEnvelope envelope, CancellationToken cancellationToken);
	}

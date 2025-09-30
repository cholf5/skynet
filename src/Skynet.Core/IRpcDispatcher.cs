using System.Threading;
using System.Threading.Tasks;

namespace Skynet.Core;

public interface IRpcDispatcher<TContract> where TContract : class
	{
	Task<object?> DispatchAsync(TContract target, MessageEnvelope envelope, CancellationToken cancellationToken);
	}

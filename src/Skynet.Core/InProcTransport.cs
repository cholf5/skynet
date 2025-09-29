using System.Threading.Tasks;
namespace Skynet.Core;

/// <summary>
/// A transport that directly routes envelopes to actors within the same process.
/// </summary>
public sealed class InProcTransport : ITransport
{
	private readonly ActorSystem _system;

	/// <summary>
	/// Initializes a new instance of the <see cref="InProcTransport"/> class.
	/// </summary>
	/// <param name="system">The actor system that will receive the envelopes.</param>
	public InProcTransport(ActorSystem system)
	{
		_system = system;
	}

	/// <inheritdoc />
	public ValueTask SendAsync(MessageEnvelope envelope, TaskCompletionSource<object?>? response, CancellationToken cancellationToken = default)
	{
		return _system.DeliverLocalAsync(envelope, response, cancellationToken);
	}
}

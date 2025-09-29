using System.Threading.Tasks;

namespace Skynet.Core;

/// <summary>
/// Provides an abstraction over transport implementations that can deliver envelopes to actors.
/// </summary>
public interface ITransport
{
	/// <summary>
	/// Sends the specified envelope to its destination.
	/// </summary>
	/// <param name="envelope">The envelope to send.</param>
	/// <param name="response">An optional completion source that must be completed with the response payload.</param>
	/// <param name="cancellationToken">Token used to cancel the send operation.</param>
	/// <returns>A task that completes when the envelope has been accepted for delivery.</returns>
	ValueTask SendAsync(MessageEnvelope envelope, TaskCompletionSource<object?>? response, CancellationToken cancellationToken = default);
}

using System.Threading.Tasks;

namespace Skynet.Core;

internal sealed class MailboxMessage
{
	public MailboxMessage(MessageEnvelope envelope, TaskCompletionSource<object?>? completion)
	{
		Envelope = envelope;
		Completion = completion;
	}

	public MessageEnvelope Envelope { get; }

	public TaskCompletionSource<object?>? Completion { get; }
}

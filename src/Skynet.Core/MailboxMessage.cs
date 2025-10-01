namespace Skynet.Core;

internal sealed class MailboxMessage(MessageEnvelope envelope, TaskCompletionSource<object?>? completion)
{
	public MessageEnvelope Envelope { get; } = envelope;

	public TaskCompletionSource<object?>? Completion { get; } = completion;
}

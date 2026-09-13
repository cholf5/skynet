namespace Skynet.Core;

internal sealed class MailboxMessage
{
	public MailboxMessage(MessageEnvelope envelope, TaskCompletionSource<object?>? completion)
	{
		Envelope = envelope;
		Completion = completion;
	}

	/// <summary>
	/// Creates a system message (e.g. a due timer tick) whose callback is executed by the
	/// target actor's message loop instead of being dispatched through <see cref="Actor.ReceiveAsync"/>.
	/// </summary>
	public MailboxMessage(MessageEnvelope envelope, Func<CancellationToken, ValueTask> systemCallback)
	{
		Envelope = envelope;
		SystemCallback = systemCallback;
	}

	public MessageEnvelope Envelope { get; }

	public TaskCompletionSource<object?>? Completion { get; }

	/// <summary>
	/// Gets the system callback to execute when set; regular messages leave this as <see langword="null"/>.
	/// </summary>
	public Func<CancellationToken, ValueTask>? SystemCallback { get; }
}

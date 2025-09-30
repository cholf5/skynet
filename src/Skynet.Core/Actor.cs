using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Skynet.Core;

/// <summary>
/// Base type for all actors hosted inside an <see cref="ActorSystem"/>.
/// </summary>
public abstract class Actor : IAsyncDisposable
{
	private ActorHost? _host;

	/// <summary>
	/// Gets the handle of the current actor.
	/// </summary>
	protected ActorHandle Self => _host?.Handle ?? throw new InvalidOperationException("Actor is not attached to a host.");

	/// <summary>
	/// Gets the <see cref="ActorSystem"/> the actor is hosted in.
	/// </summary>
	protected ActorSystem System => _host?.System ?? throw new InvalidOperationException("Actor is not attached to a host.");

	/// <summary>
	/// Gets the logger associated with the actor.
	/// </summary>
	protected ILogger Logger => _host?.Logger ?? NullLogger.Instance;

	internal void Attach(ActorHost host)
	{
		_host = host;
	}

	internal ValueTask OnStartAsync(CancellationToken cancellationToken)
	{
		return HandleStartAsync(cancellationToken);
	}

	internal ValueTask OnStopAsync(CancellationToken cancellationToken)
	{
		return HandleStopAsync(cancellationToken);
	}

	internal ValueTask OnErrorAsync(MessageEnvelope envelope, Exception exception, CancellationToken cancellationToken)
	{
		return HandleErrorAsync(envelope, exception, cancellationToken);
	}

	internal Task<object?> ReceiveInternalAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
	{
		return ReceiveAsync(envelope, cancellationToken);
	}

	/// <summary>
	/// Called when the actor is started.
	/// </summary>
	/// <param name="cancellationToken">A token that is cancelled when the actor is shutting down.</param>
	/// <returns>A task that represents the asynchronous operation.</returns>
	protected virtual ValueTask HandleStartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

	/// <summary>
	/// Called when the actor is stopping.
	/// </summary>
	/// <param name="cancellationToken">A token that is cancelled when the actor is shutting down.</param>
	/// <returns>A task that represents the asynchronous operation.</returns>
	protected virtual ValueTask HandleStopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

	/// <summary>
	/// Called when an exception is thrown while processing a message.
	/// </summary>
	/// <param name="envelope">The envelope that was being processed.</param>
	/// <param name="exception">The exception that was thrown.</param>
	/// <param name="cancellationToken">A token that is cancelled when the actor is shutting down.</param>
	/// <returns>A task that represents the asynchronous operation.</returns>
	protected virtual ValueTask HandleErrorAsync(MessageEnvelope envelope, Exception exception, CancellationToken cancellationToken)
	{
		Logger.LogError(exception, "Actor {Handle} failed to process message {MessageId}.", Self.Value, envelope.MessageId);
		return ValueTask.CompletedTask;
	}

	/// <summary>
	/// Processes a message delivered to the actor.
	/// </summary>
	/// <param name="envelope">The envelope that contains the payload and metadata.</param>
	/// <param name="cancellationToken">A token that is cancelled when the actor is shutting down.</param>
	/// <returns>The response payload when the message is handled via <see cref="CallType.Call"/>.</returns>
	protected abstract Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken);

	/// <inheritdoc />
	public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

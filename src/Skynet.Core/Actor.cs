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

	/// <summary>
	/// Registers a one-shot timer whose callback is executed by this actor's message loop
	/// after <paramref name="dueTime"/> has elapsed. The callback is serialized with all other
	/// messages of this actor. Timers are cancelled automatically when the actor is stopped.
	/// </summary>
	/// <param name="dueTime">Delay before the callback runs.</param>
	/// <param name="callback">The callback to execute. Registration is thread-safe and may happen from any thread.</param>
	/// <returns>A handle that can be passed to <see cref="CancelTimer"/>.</returns>
	/// <exception cref="InvalidOperationException">Thrown when the actor is not attached to a host.</exception>
	protected TimerHandle AddTimer(TimeSpan dueTime, Func<CancellationToken, ValueTask> callback)
	{
		ArgumentNullException.ThrowIfNull(callback);
		if (dueTime < TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(dueTime), "The timer due time must not be negative.");
		}

		var host = _host ?? throw new InvalidOperationException("Actor is not attached to a host.");
		return host.System.Timers.Register(host.Handle, dueTime, interval: null, callback);
	}

	/// <summary>
	/// Registers a periodic timer whose callback is executed by this actor's message loop every
	/// <paramref name="interval"/>. Ticks are coalesced: while a previous callback is still queued
	/// or executing in this actor, no additional tick is dispatched. Timers are cancelled
	/// automatically when the actor is stopped.
	/// </summary>
	/// <param name="interval">Repeat interval; also the delay before the first callback.</param>
	/// <param name="callback">The callback to execute.</param>
	/// <returns>A handle that can be passed to <see cref="CancelTimer"/>.</returns>
	/// <exception cref="InvalidOperationException">Thrown when the actor is not attached to a host.</exception>
	protected TimerHandle SchedulePeriodic(TimeSpan interval, Func<CancellationToken, ValueTask> callback)
	{
		ArgumentNullException.ThrowIfNull(callback);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
		var host = _host ?? throw new InvalidOperationException("Actor is not attached to a host.");
		return host.System.Timers.Register(host.Handle, interval, interval, callback);
	}

	/// <summary>
	/// Cancels a timer previously registered by this actor. Safe to call from any thread.
	/// </summary>
	/// <param name="timer">The handle returned by <see cref="AddTimer"/> or <see cref="SchedulePeriodic"/>.</param>
	/// <returns><see langword="true"/> when the timer was still pending and has been cancelled;
	/// <see langword="false"/> when it already fired or was registered by a different actor.</returns>
	protected bool CancelTimer(TimerHandle timer)
	{
		var host = _host;
		return host is not null && host.System.Timers.Cancel(host.Handle, timer);
	}

	/// <inheritdoc />
	public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

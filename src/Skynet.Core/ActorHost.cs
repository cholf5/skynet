using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Skynet.Core;

internal sealed class ActorHost : IAsyncDisposable
{
	private readonly Channel<MailboxMessage> _mailbox;
	private readonly ActorMetricsCollector _metrics;
	private readonly CancellationTokenSource _cts = new();
	private readonly Task _loop;
	private readonly TaskCompletionSource<bool> _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly TaskCompletionSource<bool> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

	internal ActorHost(ActorSystem system, ActorHandle handle, Actor actor, ILogger logger, string? name, ActorMetricsCollector metrics)
	{
		ArgumentNullException.ThrowIfNull(metrics);
		System = system;
		Handle = handle;
		Actor = actor;
		Logger = logger;
		_metrics = metrics;
		_mailbox = Channel.CreateUnbounded<MailboxMessage>(new UnboundedChannelOptions
		{
			SingleReader = true,
			SingleWriter = false,
			AllowSynchronousContinuations = false
		});
		_metrics.RegisterActor(handle, name, actor.GetType());
		Actor.Attach(this);
		_loop = Task.Run(RunAsync);
	}

	internal ActorSystem System { get; }

	internal ActorHandle Handle { get; }

	internal Actor Actor { get; }

	internal ILogger Logger { get; }

	internal Task Startup => _startup.Task;

	public ValueTask EnqueueAsync(MailboxMessage message, CancellationToken cancellationToken)
	{
		_metrics.OnMessageEnqueued(Handle);
		return EnqueueInternalAsync(message, cancellationToken);
	}

	private async ValueTask EnqueueInternalAsync(MailboxMessage message, CancellationToken cancellationToken)
	{
		try
		{
			await _mailbox.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			_metrics.OnMessageDequeued(Handle);
			throw;
		}
	}

	/// <summary>
	/// Enqueues a system message (e.g. a due timer tick) without awaiting. Used by the timer
	/// scheduler thread, where the unbounded mailbox write always completes synchronously.
	/// Returns <see langword="false"/> when the actor has already shut down and the message is dropped.
	/// </summary>
	internal bool TryEnqueueSystemMessage(MailboxMessage message)
	{
		_metrics.OnMessageEnqueued(Handle);
		return _mailbox.Writer.TryWrite(message);
	}

	private async Task RunAsync()
	{
		try
		{
			await Actor.OnStartAsync(_cts.Token).ConfigureAwait(false);
			_startup.TrySetResult(true);
		}
		catch (Exception ex)
		{
			_startup.TrySetException(ex);
			_stopped.TrySetResult(true);
			return;
		}

		try
		{
			while (await _mailbox.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
			{
				while (_mailbox.Reader.TryRead(out var message))
				{
					await ProcessMessageAsync(message).ConfigureAwait(false);
				}
			}
		}
		catch (OperationCanceledException) when (_cts.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			Logger.LogError(ex, "Actor {Handle} stopped unexpectedly.", Handle.Value);
		}
		finally
		{
			await ShutdownAsync().ConfigureAwait(false);
			_stopped.TrySetResult(true);
		}
	}

	private async Task ProcessMessageAsync(MailboxMessage message)
	{
		_metrics.OnMessageDequeued(Handle);
		var envelope = message.Envelope;
		// Establish this actor's position in the causal call chain for the lifetime of the
		// message. The envelope carries the chain of actors suspended on this call (in-process
		// calls only); appending our own handle lets nested CallAsync invocations detect cycles
		// that would deadlock the strictly serial mailbox. System callbacks (timers) run on the
		// same serial loop, so they participate in the chain as well.
		using var callChainScope = ActorCallContext.BeginScope(
			ActorCallChain.Push(envelope.CallChain, envelope.To));
		if (message.SystemCallback is not null)
		{
			await ProcessSystemCallbackAsync(message.SystemCallback, envelope).ConfigureAwait(false);
			return;
		}

		using var scope = TraceContext.BeginScope(envelope.TraceId);
		var stopwatch = Stopwatch.StartNew();
		var traceEnabled = _metrics.IsTracing(Handle);
		if (traceEnabled)
		{
			Logger.LogInformation("Trace[{Handle}] >> {MessageId} {CallType} {PayloadType}", Handle.Value, envelope.MessageId, envelope.CallType, envelope.Payload?.GetType().Name ?? "<null>");
		}

		try
		{
			var result = await Actor.ReceiveInternalAsync(envelope, _cts.Token).ConfigureAwait(false);
			message.Completion?.TrySetResult(result);
			_metrics.OnMessageProcessed(Handle, stopwatch.Elapsed, true);
			if (traceEnabled)
			{
				Logger.LogInformation("Trace[{Handle}] << {MessageId} completed in {Elapsed} ms", Handle.Value, envelope.MessageId, stopwatch.Elapsed.TotalMilliseconds);
			}
		}
		catch (OperationCanceledException)
		{
			message.Completion?.TrySetCanceled(_cts.Token);
			_metrics.OnMessageProcessed(Handle, stopwatch.Elapsed, true);
			if (traceEnabled)
			{
				Logger.LogWarning("Trace[{Handle}] !! {MessageId} canceled after {Elapsed} ms", Handle.Value, envelope.MessageId, stopwatch.Elapsed.TotalMilliseconds);
			}
		}
		catch (Exception ex)
		{
			message.Completion?.TrySetException(ex);
			_metrics.OnMessageProcessed(Handle, stopwatch.Elapsed, false);
			if (traceEnabled)
			{
				Logger.LogError(ex, "Trace[{Handle}] xx {MessageId} failed after {Elapsed} ms", Handle.Value, envelope.MessageId, stopwatch.Elapsed.TotalMilliseconds);
			}
			try
			{
				await Actor.OnErrorAsync(envelope, ex, _cts.Token).ConfigureAwait(false);
			}
			catch (Exception hookEx)
			{
				Logger.LogError(hookEx, "Actor {Handle} error hook failed.", Handle.Value);
			}
		}
	}

	private async Task ProcessSystemCallbackAsync(Func<CancellationToken, ValueTask> callback, MessageEnvelope envelope)
	{
		var stopwatch = Stopwatch.StartNew();
		try
		{
			await callback(_cts.Token).ConfigureAwait(false);
			_metrics.OnMessageProcessed(Handle, stopwatch.Elapsed, true);
		}
		catch (OperationCanceledException)
		{
			_metrics.OnMessageProcessed(Handle, stopwatch.Elapsed, true);
		}
		catch (Exception ex)
		{
			_metrics.OnMessageProcessed(Handle, stopwatch.Elapsed, false);
			Logger.LogError(ex, "Actor {Handle} timer callback for message {MessageId} failed.", Handle.Value, envelope.MessageId);
			try
			{
				await Actor.OnErrorAsync(envelope, ex, _cts.Token).ConfigureAwait(false);
			}
			catch (Exception hookEx)
			{
				Logger.LogError(hookEx, "Actor {Handle} error hook failed.", Handle.Value);
			}
		}
	}

	private async ValueTask ShutdownAsync()
	{
		try
		{
			_mailbox.Writer.TryComplete();
			await Actor.OnStopAsync(CancellationToken.None).ConfigureAwait(false);
			await Actor.DisposeAsync().ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			Logger.LogError(ex, "Actor {Handle} failed during shutdown.", Handle.Value);
		}

		_metrics.UnregisterActor(Handle);
	}

	public async ValueTask DisposeAsync()
	{
		await _cts.CancelAsync();
		_mailbox.Writer.TryComplete();
		try
		{
			await _stopped.Task.ConfigureAwait(false);
			await _loop.ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			_cts.Dispose();
		}
	}
}

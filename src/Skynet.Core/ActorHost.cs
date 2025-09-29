using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Skynet.Core;

internal sealed class ActorHost : IAsyncDisposable
{
	private readonly Channel<MailboxMessage> _mailbox;
	private readonly CancellationTokenSource _cts = new();
	private readonly Task _loop;
	private readonly TaskCompletionSource<bool> _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly TaskCompletionSource<bool> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

	internal ActorHost(ActorSystem system, ActorHandle handle, Actor actor, ILogger logger)
	{
		System = system;
		Handle = handle;
		Actor = actor;
		Logger = logger;
		_mailbox = Channel.CreateUnbounded<MailboxMessage>(new UnboundedChannelOptions
		{
			SingleReader = true,
			SingleWriter = false,
			AllowSynchronousContinuations = false
		});
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
		return _mailbox.Writer.WriteAsync(message, cancellationToken);
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
		try
		{
			var result = await Actor.ReceiveInternalAsync(message.Envelope, _cts.Token).ConfigureAwait(false);
			message.Completion?.TrySetResult(result);
		}
		catch (OperationCanceledException)
		{
			message.Completion?.TrySetCanceled(_cts.Token);
		}
		catch (Exception ex)
		{
			message.Completion?.TrySetException(ex);
			try
			{
				await Actor.OnErrorAsync(message.Envelope, ex, _cts.Token).ConfigureAwait(false);
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
	}

	public async ValueTask DisposeAsync()
	{
		_cts.Cancel();
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

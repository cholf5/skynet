using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Skynet.Core;

/// <summary>
/// Provides an in-process transport that can either short-circuit delivery or hop through a mailbox queue.
/// </summary>
public sealed class InProcTransport : ITransport, IAsyncDisposable
{
	private readonly ActorSystem _system;
	private readonly bool _shortCircuit;
	private readonly Channel<PendingDelivery>? _queue;
	private readonly CancellationTokenSource? _pumpCancellation;
	private readonly Task? _pumpTask;
	private bool _disposed;

	/// <summary>
	/// Initializes a new instance of the <see cref="InProcTransport"/> class.
	/// </summary>
	/// <param name="system">The owning actor system.</param>
	/// <param name="options">Behavioral options.</param>
	public InProcTransport(ActorSystem system, InProcTransportOptions? options = null)
	{
		_system = system ?? throw new ArgumentNullException(nameof(system));
		var effectiveOptions = options ?? InProcTransportOptions.Default;
		_shortCircuit = effectiveOptions.ShortCircuitLocalDelivery;
		if (_shortCircuit)
		{
			return;
		}

		_queue = Channel.CreateUnbounded<PendingDelivery>(new UnboundedChannelOptions
		{
			SingleReader = true,
			SingleWriter = false,
			AllowSynchronousContinuations = false
		});
		_pumpCancellation = new CancellationTokenSource();
		_pumpTask = Task.Run(() => PumpAsync(_pumpCancellation.Token));
	}

	/// <inheritdoc />
	public ValueTask SendAsync(MessageEnvelope envelope, TaskCompletionSource<object?>? response, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (_disposed)
		{
			return ValueTask.FromException(new ObjectDisposedException(nameof(InProcTransport)));
		}

		if (_shortCircuit)
		{
			return _system.DeliverLocalAsync(envelope, response, cancellationToken);
		}

		var pending = new PendingDelivery(envelope, response, cancellationToken);
		return EnqueueAsync(pending, cancellationToken);
	}

	private ValueTask EnqueueAsync(PendingDelivery pending, CancellationToken cancellationToken)
	{
		if (_queue is null)
		{
			pending.Dispose();
			return ValueTask.FromException(new ObjectDisposedException(nameof(InProcTransport)));
		}

		if (_queue.Writer.TryWrite(pending))
		{
			return ValueTask.CompletedTask;
		}

		return new ValueTask(_queue.Writer.WriteAsync(pending, cancellationToken).AsTask());
	}

	private async Task PumpAsync(CancellationToken cancellationToken)
	{
		if (_queue is null)
		{
			return;
		}

		try
		{
			await foreach (var pending in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
			{
				using (pending)
				{
					if (pending.TryCompleteIfCanceled())
					{
						continue;
					}

					try
					{
						await _system.DeliverLocalAsync(pending.Envelope, pending.Response, pending.CancellationToken).ConfigureAwait(false);
					}
					catch (OperationCanceledException)
					{
						pending.TrySetCanceled();
					}
					catch (Exception ex)
					{
						pending.TrySetException(ex);
					}
				}
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
	}

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		if (_shortCircuit)
		{
			return;
		}

		_pumpCancellation?.Cancel();
		_queue?.Writer.TryComplete();
		if (_pumpTask is not null)
		{
			try
			{
				await _pumpTask.ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (_pumpCancellation?.IsCancellationRequested == true)
			{
			}
		}

		_pumpCancellation?.Dispose();
	}

	private sealed class PendingDelivery : IDisposable
	{
		private readonly CancellationTokenRegistration _registration;
		private int _completed;

		internal PendingDelivery(MessageEnvelope envelope, TaskCompletionSource<object?>? response, CancellationToken cancellationToken)
		{
			Envelope = envelope;
			Response = response;
			CancellationToken = cancellationToken;
			if (response is not null && cancellationToken.CanBeCanceled)
			{
				_registration = cancellationToken.Register(static state =>
				{
					var pending = (PendingDelivery)state!;
					pending.TrySetCanceled();
				}, this);
			}
		}

		internal MessageEnvelope Envelope { get; }
		internal TaskCompletionSource<object?>? Response { get; }
		internal CancellationToken CancellationToken { get; }

		public void Dispose()
		{
			_registration.Dispose();
		}

		internal bool TryCompleteIfCanceled()
		{
			if (!CancellationToken.IsCancellationRequested)
			{
				return false;
			}

			TrySetCanceled();
			return true;
		}

		internal void TrySetCanceled()
		{
			if (Response is null)
			{
				return;
			}

			if (Interlocked.Exchange(ref _completed, 1) == 1)
			{
				return;
			}

			if (CancellationToken.CanBeCanceled)
			{
				Response.TrySetCanceled(CancellationToken);
			}
			else
			{
				Response.TrySetCanceled();
			}
		}

		internal void TrySetException(Exception exception)
		{
			if (Response is null)
			{
				return;
			}

			if (Interlocked.Exchange(ref _completed, 1) == 1)
			{
				return;
			}

			Response.TrySetException(exception);
		}
	}
}

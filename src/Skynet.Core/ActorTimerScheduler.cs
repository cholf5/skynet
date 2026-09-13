namespace Skynet.Core;

/// <summary>
/// Dispatches due timer callbacks as system messages into the owning actor's mailbox.
/// <para>
/// Concurrency model: a single background scheduler thread owns a priority queue of pending
/// timers. Registration and cancellation are thread-safe from any thread (a lock-protected
/// index takes effect immediately, so <c>CancelTimer</c> synchronously reports whether the
/// timer was still pending), and user callbacks never run on the scheduler thread - they are
/// delivered to the target actor's mailbox and executed by its message loop, serialized with
/// all other messages of that actor.
/// </para>
/// <para>
/// Periodic timers coalesce ticks: while a previous tick is still queued or executing in the
/// actor, the scheduler skips dispatching additional ticks instead of letting them pile up.
/// </para>
/// </summary>
internal sealed class ActorTimerScheduler : IDisposable
{
	private readonly ActorSystem _system;
	private readonly Lock _stateLock = new();
	private readonly PriorityQueue<long, DateTimeOffset> _queue = new();
	private readonly Dictionary<long, TimerEntry> _timers = new();
	private readonly AutoResetEvent _wakeup = new(false);
	private readonly Thread _thread;
	private long _nextTimerId;
	private bool _disposed;

	/// <summary>
	/// Initializes a new instance of the <see cref="ActorTimerScheduler"/> class and starts its background thread.
	/// </summary>
	/// <param name="system">The owning actor system, used for mailbox access and liveness checks.</param>
	public ActorTimerScheduler(ActorSystem system)
	{
		_system = system;
		_thread = new Thread(Run)
		{
			IsBackground = true,
			Name = "Skynet.TimerScheduler"
		};
		_thread.Start();
	}

	/// <summary>
	/// Gets the number of timers currently registered. Intended for diagnostics and tests.
	/// </summary>
	public int ActiveTimerCount
	{
		get
		{
			lock (_stateLock)
			{
				return _timers.Count;
			}
		}
	}

	/// <summary>
	/// Registers a timer for an actor. Safe to call from any thread.
	/// </summary>
	/// <param name="owner">The actor that owns the timer and receives the due callback.</param>
	/// <param name="dueTime">Delay before the first (and, for one-shot timers, only) callback.</param>
	/// <param name="interval">Repeat interval for periodic timers, or <see langword="null"/> for one-shot timers.</param>
	/// <param name="callback">The callback executed inside the owner actor's message loop.</param>
	/// <returns>A handle that can be used to cancel the timer.</returns>
	public TimerHandle Register(ActorHandle owner, TimeSpan dueTime, TimeSpan? interval, Func<CancellationToken, ValueTask> callback)
	{
		lock (_stateLock)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (!_system.TryGetActorHost(owner, out _))
			{
				throw new InvalidOperationException($"Actor {owner.Value} is no longer running; timers cannot be registered for it.");
			}

			var id = ++_nextTimerId;
			var due = DateTimeOffset.UtcNow + dueTime;
			_timers[id] = new TimerEntry
			{
				Id = id,
				Owner = owner,
				Callback = callback,
				Interval = interval,
				DueTime = due
			};
			_queue.Enqueue(id, due);
			_wakeup.Set();
			return new TimerHandle(id);
		}
	}

	/// <summary>
	/// Cancels a single timer. Safe to call from any thread.
	/// </summary>
	/// <param name="owner">The actor that is expected to own the timer.</param>
	/// <param name="timer">The handle returned by <see cref="Register"/>.</param>
	/// <returns><see langword="true"/> when the timer was still pending and has been cancelled.</returns>
	public bool Cancel(ActorHandle owner, TimerHandle timer)
	{
		lock (_stateLock)
		{
			if (!_timers.TryGetValue(timer.Id, out var entry) || entry.Owner != owner)
			{
				return false;
			}

			_timers.Remove(timer.Id);
			return true;
			// The stale queue entry is skipped lazily when it surfaces at the top of the heap.
		}
	}

	/// <summary>
	/// Cancels every timer owned by the specified actor. Called when the actor is removed from the system.
	/// </summary>
	/// <param name="owner">The actor handle whose timers should be cancelled.</param>
	/// <returns>The number of timers that were still pending and have been cancelled.</returns>
	public int CancelByActor(ActorHandle owner)
	{
		lock (_stateLock)
		{
			var removed = 0;
			foreach (var id in _timers.Keys.ToArray())
			{
				if (_timers[id].Owner == owner)
				{
					_timers.Remove(id);
					removed++;
				}
			}

			return removed;
		}
	}

	/// <summary>
	/// Clears the pending-tick marker of a periodic timer after its callback finished executing.
	/// Called from the owner actor's message loop; unknown ids are ignored (the timer may have been cancelled).
	/// </summary>
	/// <param name="timerId">The periodic timer id.</param>
	public void OnTickCompleted(long timerId)
	{
		lock (_stateLock)
		{
			if (_timers.TryGetValue(timerId, out var entry))
			{
				entry.TickPending = false;
			}
		}
	}

	/// <inheritdoc />
	public void Dispose()
	{
		lock (_stateLock)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			_timers.Clear();
			_queue.Clear();
		}

		_wakeup.Set();
		if (Thread.CurrentThread != _thread)
		{
			_thread.Join();
		}

		_wakeup.Dispose();
	}

	private void Run()
	{
		while (true)
		{
			List<MailboxMessage> due;
			int waitMilliseconds;
			lock (_stateLock)
			{
				if (_disposed)
				{
					return;
				}

				due = CollectDueMessagesUnsafe(DateTimeOffset.UtcNow);
				waitMilliseconds = ComputeWaitUnsafe();
			}

			Dispatch(due);
			if (due.Count == 0)
			{
				_wakeup.WaitOne(waitMilliseconds);
			}
		}
	}

	private List<MailboxMessage> CollectDueMessagesUnsafe(DateTimeOffset now)
	{
		var messages = new List<MailboxMessage>();
		while (_queue.TryPeek(out var timerId, out var dueTime) && dueTime <= now)
		{
			_queue.Dequeue();
			if (!_timers.TryGetValue(timerId, out var entry) || entry.DueTime != dueTime)
			{
				// Cancelled or superseded (periodic reschedule) - lazily dropped.
				continue;
			}

			if (entry.Interval.HasValue)
			{
				var nextDue = DateTimeOffset.UtcNow + entry.Interval.Value;
				entry.DueTime = nextDue;
				_queue.Enqueue(timerId, nextDue);
				if (entry.TickPending)
				{
					// Coalesce: the previous tick is still queued or executing in the actor.
					continue;
				}

				entry.TickPending = true;
			}
			else
			{
				_timers.Remove(timerId);
			}

			messages.Add(CreateTickMessage(entry));
		}

		return messages;
	}

	private int ComputeWaitUnsafe()
	{
		if (!_queue.TryPeek(out _, out var nextDue))
		{
			return Timeout.Infinite;
		}

		var delta = (nextDue - DateTimeOffset.UtcNow).TotalMilliseconds;
		return delta <= 0 ? 0 : (int)Math.Min(delta, int.MaxValue - 1);
	}

	private MailboxMessage CreateTickMessage(TimerEntry entry)
	{
		var envelope = _system.CreateEnvelope(entry.Owner, ActorHandle.None, CallType.Send, new TimerFired(entry.Id));
		if (!entry.Interval.HasValue)
		{
			return new MailboxMessage(envelope, entry.Callback);
		}

		// Periodic ticks clear the pending marker once the callback finished, so the
		// scheduler can dispatch the next due tick. Unknown ids are ignored on purpose.
		return new MailboxMessage(envelope, async token =>
		{
			try
			{
				await entry.Callback(token).ConfigureAwait(false);
			}
			finally
			{
				OnTickCompleted(entry.Id);
			}
		});
	}

	private void Dispatch(List<MailboxMessage> messages)
	{
		foreach (var message in messages)
		{
			if (_system.TryGetActorHost(message.Envelope.To, out var host))
			{
				host.TryEnqueueSystemMessage(message);
			}

			// The owner may have been removed between collection and dispatch; the tick is dropped.
		}
	}

	private sealed class TimerEntry
	{
		public required long Id { get; init; }

		public required ActorHandle Owner { get; init; }

		public required Func<CancellationToken, ValueTask> Callback { get; init; }

		public required TimeSpan? Interval { get; init; }

		public DateTimeOffset DueTime { get; set; }

		public bool TickPending { get; set; }
	}
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Skynet.Core;

/// <summary>
/// Collects runtime metrics for actors hosted within an <see cref="ActorSystem"/>.
/// </summary>
public sealed class ActorMetricsCollector
{
	private readonly ConcurrentDictionary<long, ActorMetricsEntry> _entries = new();

	/// <summary>
	/// Registers a new actor with the metrics collector.
	/// </summary>
	public void RegisterActor(ActorHandle handle, string? name, Type implementationType)
	{
		ArgumentNullException.ThrowIfNull(implementationType);
		var entry = new ActorMetricsEntry(handle, name, implementationType);
		_entries[handle.Value] = entry;
	}

	/// <summary>
	/// Removes an actor from the metrics collector.
	/// </summary>
	public void UnregisterActor(ActorHandle handle)
	{
		_entries.TryRemove(handle.Value, out _);
	}

	/// <summary>
	/// Records that a message has been enqueued for the specified actor.
	/// </summary>
	public void OnMessageEnqueued(ActorHandle handle)
	{
		if (_entries.TryGetValue(handle.Value, out var entry))
		{
			entry.OnMessageEnqueued();
		}
	}

	/// <summary>
	/// Records that a message has been dequeued for processing.
	/// </summary>
	public void OnMessageDequeued(ActorHandle handle)
	{
		if (_entries.TryGetValue(handle.Value, out var entry))
		{
			entry.OnMessageDequeued();
		}
	}

	/// <summary>
	/// Records the outcome of a processed message.
	/// </summary>
	public void OnMessageProcessed(ActorHandle handle, TimeSpan duration, bool success)
	{
		if (_entries.TryGetValue(handle.Value, out var entry))
		{
			entry.OnMessageProcessed(duration, success);
		}
	}

	/// <summary>
	/// Enables tracing for the specified actor.
	/// </summary>
	public bool EnableTrace(ActorHandle handle)
	{
		return _entries.TryGetValue(handle.Value, out var entry) && entry.EnableTrace();
	}

	/// <summary>
	/// Disables tracing for the specified actor.
	/// </summary>
	public bool DisableTrace(ActorHandle handle)
	{
		return _entries.TryGetValue(handle.Value, out var entry) && entry.DisableTrace();
	}

	/// <summary>
	/// Determines whether tracing is enabled for the specified actor.
	/// </summary>
	public bool IsTracing(ActorHandle handle)
	{
		return _entries.TryGetValue(handle.Value, out var entry) && entry.TraceEnabled;
	}

	/// <summary>
	/// Obtains a snapshot for a specific actor.
	/// </summary>
	public bool TryGetSnapshot(ActorHandle handle, out ActorMetricsSnapshot snapshot)
	{
		if (_entries.TryGetValue(handle.Value, out var entry))
		{
			snapshot = entry.CreateSnapshot();
			return true;
		}

		snapshot = default!;
		return false;
	}

	/// <summary>
	/// Returns a snapshot of all registered actors and their metrics.
	/// </summary>
	public IReadOnlyCollection<ActorMetricsSnapshot> GetSnapshot()
	{
		var entries = _entries.Values.ToArray();
		var result = new List<ActorMetricsSnapshot>(entries.Length);
		foreach (var entry in entries)
		{
			result.Add(entry.CreateSnapshot());
		}
		return result;
	}

	private sealed class ActorMetricsEntry
	{
		private readonly ActorHandle _handle;
		private readonly string? _name;
		private readonly Type _implementationType;
		private readonly DateTimeOffset _createdAt = DateTimeOffset.UtcNow;
		private long _queueLength;
		private long _processedCount;
		private long _exceptionCount;
		private long _totalProcessingTicks;
		private long _lastEnqueuedTicks;
		private long _lastProcessedTicks;
		private int _traceEnabled;

		public ActorMetricsEntry(ActorHandle handle, string? name, Type implementationType)
		{
			_handle = handle;
			_name = name;
			_implementationType = implementationType;
		}

		public bool TraceEnabled => Volatile.Read(ref _traceEnabled) == 1;

		public void OnMessageEnqueued()
		{
			Interlocked.Increment(ref _queueLength);
			Interlocked.Exchange(ref _lastEnqueuedTicks, DateTimeOffset.UtcNow.UtcTicks);
		}

		public void OnMessageDequeued()
		{
			InterlockedExtensions.SafeDecrement(ref _queueLength);
		}

		public void OnMessageProcessed(TimeSpan duration, bool success)
		{
			Interlocked.Increment(ref _processedCount);
			Interlocked.Add(ref _totalProcessingTicks, duration.Ticks);
			Interlocked.Exchange(ref _lastProcessedTicks, DateTimeOffset.UtcNow.UtcTicks);
			if (!success)
			{
				Interlocked.Increment(ref _exceptionCount);
			}
		}

		public bool EnableTrace()
		{
			return Interlocked.Exchange(ref _traceEnabled, 1) == 0;
		}

		public bool DisableTrace()
		{
			return Interlocked.Exchange(ref _traceEnabled, 0) == 1;
		}

		public ActorMetricsSnapshot CreateSnapshot()
		{
			var processed = Interlocked.Read(ref _processedCount);
			var totalTicks = Interlocked.Read(ref _totalProcessingTicks);
			var average = processed == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Max(0, totalTicks / processed));
			var lastEnqueued = Interlocked.Read(ref _lastEnqueuedTicks);
			var lastProcessed = Interlocked.Read(ref _lastProcessedTicks);
			return new ActorMetricsSnapshot(
			_handle,
			_name,
			_implementationType,
			Interlocked.Read(ref _queueLength),
			processed,
			Interlocked.Read(ref _exceptionCount),
			average,
			lastEnqueued > 0 ? new DateTimeOffset(lastEnqueued, TimeSpan.Zero) : null,
			lastProcessed > 0 ? new DateTimeOffset(lastProcessed, TimeSpan.Zero) : null,
			_createdAt,
			TraceEnabled);
		}
	}
}

internal static class InterlockedExtensions
{
	public static long SafeDecrement(ref long location)
	{
		long initial;
		long computed;
		do
		{
			initial = Volatile.Read(ref location);
			if (initial == 0)
			{
				return 0;
			}
			computed = initial - 1;
		}
		while (Interlocked.CompareExchange(ref location, computed, initial) != initial);
		return computed;
	}
}

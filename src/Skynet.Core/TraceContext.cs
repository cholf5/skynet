using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Skynet.Core;

/// <summary>
/// Provides a lightweight ambient trace identifier flow that mirrors Activity semantics
/// without taking a dependency on diagnostic listeners.
/// </summary>
public static class TraceContext
{
	private static readonly AsyncLocal<string?> _traceId = new();

	/// <summary>
	/// Gets or sets the trace identifier associated with the current asynchronous flow.
	/// </summary>
	public static string? CurrentTraceId
	{
		get => _traceId.Value;
		set => _traceId.Value = value;
	}

	/// <summary>
	/// Ensures a trace identifier is present on the current execution context and returns it.
	/// </summary>
	/// <returns>The existing or newly generated trace identifier.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static string EnsureTraceId()
	{
		var traceId = _traceId.Value;
		if (!string.IsNullOrEmpty(traceId))
		{
			return traceId;
		}

		var activity = Activity.Current;
		if (activity is not null && activity.TraceId != default)
		{
			traceId = activity.TraceId.ToString();
		}
		else
		{
			traceId = Guid.NewGuid().ToString("N");
		}

		_traceId.Value = traceId;
		return traceId;
	}

	/// <summary>
	/// Pushes a new trace identifier scope for the lifetime of the returned disposable.
	/// </summary>
	/// <param name="traceId">The trace identifier to apply. If null, the current trace identifier is preserved or generated.</param>
	/// <returns>A disposable scope that restores the previous identifier when disposed.</returns>
	public static IDisposable BeginScope(string? traceId)
	{
		var previous = _traceId.Value;
		_traceId.Value = traceId ?? previous ?? EnsureTraceId();
		return new Scope(previous);
	}

	private sealed class Scope : IDisposable
	{
		private readonly string? _previous;

		public Scope(string? previous)
		{
			_previous = previous;
		}

		public void Dispose()
		{
			_traceId.Value = _previous;
		}
	}
}

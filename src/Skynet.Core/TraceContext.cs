using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Skynet.Core;

/// <summary>
/// Provides a lightweight ambient trace identifier flow that mirrors Activity semantics
/// without taking a dependency on diagnostic listeners.
/// </summary>
public static class TraceContext
{
	private static readonly AsyncLocal<string?> TraceId = new();

	/// <summary>
	/// Gets or sets the trace identifier associated with the current asynchronous flow.
	/// </summary>
	public static string? CurrentTraceId
	{
		get => TraceId.Value;
		set => TraceId.Value = value;
	}

	/// <summary>
	/// Ensures a trace identifier is present on the current execution context and returns it.
	/// </summary>
	/// <returns>The existing or newly generated trace identifier.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static string EnsureTraceId()
	{
		var traceId = TraceId.Value;
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

		TraceId.Value = traceId;
		return traceId;
	}

	/// <summary>
	/// Pushes a new trace identifier scope for the lifetime of the returned disposable.
	/// </summary>
	/// <param name="traceId">The trace identifier to apply. If null, the current trace identifier is preserved or generated.</param>
	/// <returns>A disposable scope that restores the previous identifier when disposed.</returns>
	public static IDisposable BeginScope(string? traceId)
	{
		var previous = TraceId.Value;
		TraceId.Value = traceId ?? previous ?? EnsureTraceId();
		return new Scope(previous);
	}

	private sealed class Scope(string? previous) : IDisposable
	{
		public void Dispose()
		{
			TraceId.Value = previous;
		}
	}
}

using Microsoft.Extensions.Logging;

namespace Skynet.Cluster.Tests;

/// <summary>A single log record captured by <see cref="CapturingLogger"/>.</summary>
internal sealed record CapturedLog(LogLevel Level, Exception? Exception, string Message);

/// <summary>
/// An <see cref="ILogger"/>/<see cref="ILoggerFactory"/> pair that records every formatted log message so
/// tests can assert on self-heal and reconciliation behaviour.
/// </summary>
internal sealed class CapturingLogger : ILogger, ILoggerFactory
{
	private readonly object _gate = new();
	private readonly List<CapturedLog> _entries = new();

	public IReadOnlyList<CapturedLog> Snapshot
	{
		get
		{
			lock (_gate)
			{
				return new List<CapturedLog>(_entries);
			}
		}
	}

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
	{
		ArgumentNullException.ThrowIfNull(formatter);
		lock (_gate)
		{
			_entries.Add(new CapturedLog(logLevel, exception, formatter(state, exception)));
		}
	}

	public bool IsEnabled(LogLevel logLevel)
	{
		return true;
	}

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull
	{
		return null;
	}

	public ILogger CreateLogger(string categoryName)
	{
		return this;
	}

	public void AddProvider(ILoggerProvider provider)
	{
	}

	public void Dispose()
	{
	}
}

/// <summary>Polling helpers for deterministic waiting on background work (heartbeats, reconciliation).</summary>
internal static class TestWait
{
	public static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null)
	{
		ArgumentNullException.ThrowIfNull(condition);
		var deadline = DateTimeOffset.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(10));
		while (!condition())
		{
			if (DateTimeOffset.UtcNow > deadline)
			{
				throw new TimeoutException("The awaited condition was not met within the timeout.");
			}

			await Task.Delay(10);
		}
	}
}

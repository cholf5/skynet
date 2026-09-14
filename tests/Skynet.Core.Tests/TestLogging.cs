using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace Skynet.Core.Tests;

/// <summary>
/// Minimal in-memory <see cref="ILoggerFactory"/> shared by transport tests to assert that specific
/// log messages were (or were not) emitted.
/// </summary>
internal sealed class RecordingLoggerFactory : ILoggerFactory
{
	internal object LockObject { get; } = new();

	internal List<string> Messages { get; } = new();

	public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);

	public void AddProvider(ILoggerProvider provider)
	{
	}

	public void Dispose()
	{
	}

	internal async Task WaitForLogAsync(string fragment, TimeSpan timeout)
	{
		var deadline = DateTime.UtcNow + timeout;
		while (DateTime.UtcNow < deadline)
		{
			lock (LockObject)
			{
				if (Messages.Any(message => message.Contains(fragment, StringComparison.Ordinal)))
				{
					return;
				}
			}

			await Task.Delay(50);
		}

		lock (LockObject)
		{
			Messages.Should().Contain(message => message.Contains(fragment, StringComparison.Ordinal),
				$"expected a log entry containing '{fragment}' within {timeout.TotalSeconds}s");
		}
	}

	private sealed class RecordingLogger : ILogger
	{
		private readonly RecordingLoggerFactory _factory;

		public RecordingLogger(RecordingLoggerFactory factory)
		{
			_factory = factory;
		}

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter)
		{
			if (logLevel < LogLevel.Information)
			{
				return;
			}

			var message = formatter(state, exception);
			lock (_factory.LockObject)
			{
				_factory.Messages.Add(message);
			}
		}
	}
}

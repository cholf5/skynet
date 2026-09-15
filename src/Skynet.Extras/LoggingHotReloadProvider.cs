using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Skynet.Extras;

/// <summary>
/// An <see cref="ILoggerProvider"/> whose minimum log level can be changed at runtime without
/// restarting the process or recreating loggers. Every logger created by this provider re-reads
/// <see cref="MinimumLevel"/> on each log call, so level changes take effect immediately for
/// subsequent log writes.
/// </summary>
/// <remarks>
/// The provider wraps an optional sink <see cref="ILoggerProvider"/> that performs the actual
/// output (e.g. a console, file, or structured logging provider). When no sink is supplied the
/// created loggers forward to <see cref="NullLogger.Instance"/>, which means levels can still be
/// observed and changed through the debug console but nothing is emitted. The sink provider is
/// not disposed by this instance; its lifetime remains owned by the caller.
/// </remarks>
public sealed class LoggingHotReloadProvider : ILoggerProvider
{
	// Stored as an int so it can be exchanged atomically without a lock on the hot logging path.
	// LogLevel values are non-negative, so int.MinValue is a safe sentinel for "not configured".
	private const int NotConfigured = int.MinValue;

	private readonly ILoggerProvider? _sinkProvider;
	private int _minimumLevel;

	/// <summary>
	/// Initializes a new instance of the <see cref="LoggingHotReloadProvider"/> class.
	/// </summary>
	/// <param name="sinkProvider">
	/// Optional provider that receives the log records accepted by this provider. When omitted,
	/// records are discarded after filtering.
	/// </param>
	/// <param name="minimumLevel">Initial minimum level applied to new log writes.</param>
	public LoggingHotReloadProvider(ILoggerProvider? sinkProvider = null, LogLevel minimumLevel = LogLevel.Information)
	{
		_sinkProvider = sinkProvider;
		_minimumLevel = (int)minimumLevel;
	}

	/// <summary>
	/// Gets or sets the minimum level a log record must have to be forwarded to the sink.
	/// Setting the value to <c>null</c> disables provider-side filtering (pass-through mode:
	/// the sink alone decides what is emitted). Changes apply immediately to subsequent log calls.
	/// </summary>
	public LogLevel? MinimumLevel
	{
		get
		{
			var value = Volatile.Read(ref _minimumLevel);
			return value == NotConfigured ? null : (LogLevel)value;
		}
		set
		{
			var encoded = value.HasValue ? (int)value.Value : NotConfigured;
			Volatile.Write(ref _minimumLevel, encoded);
		}
	}

	/// <inheritdoc />
	public ILogger CreateLogger(string categoryName)
	{
		ArgumentNullException.ThrowIfNull(categoryName);
		var sinkLogger = _sinkProvider?.CreateLogger(categoryName) ?? NullLogger.Instance;
		return new ReloadableLogger(this, sinkLogger);
	}

	/// <inheritdoc />
	public void Dispose()
	{
		// The wrapped sink provider is owned by the caller; nothing to release here.
	}

	private sealed class ReloadableLogger : ILogger
	{
		private readonly LoggingHotReloadProvider _owner;
		private readonly ILogger _sinkLogger;

		public ReloadableLogger(LoggingHotReloadProvider owner, ILogger sinkLogger)
		{
			_owner = owner;
			_sinkLogger = sinkLogger;
		}

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull
		{
			return _sinkLogger.BeginScope(state);
		}

		public bool IsEnabled(LogLevel logLevel)
		{
			if (logLevel == LogLevel.None)
			{
				return false;
			}

			var minimum = _owner.MinimumLevel;
			if (minimum.HasValue && logLevel < minimum.Value)
			{
				return false;
			}

			return _sinkLogger.IsEnabled(logLevel);
		}

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			if (!IsEnabled(logLevel))
			{
				return;
			}

			_sinkLogger.Log(logLevel, eventId, state, exception, formatter);
		}
	}
}

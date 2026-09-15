using System;
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Skynet.Extras;
using Xunit;

namespace Skynet.Extras.Tests;

public class LoggingHotReloadProviderTests
{
	[Fact]
	public void MinimumLevelShouldDefaultToInformation()
	{
		var provider = new LoggingHotReloadProvider();

		provider.MinimumLevel.Should().Be(LogLevel.Information);
	}

	[Fact]
	public void MinimumLevelShouldBeChangeableAtRuntime()
	{
		var provider = new LoggingHotReloadProvider();

		provider.MinimumLevel = LogLevel.Warning;
		provider.MinimumLevel.Should().Be(LogLevel.Warning);

		provider.MinimumLevel = null;
		provider.MinimumLevel.Should().BeNull();

		provider.MinimumLevel = LogLevel.Debug;
		provider.MinimumLevel.Should().Be(LogLevel.Debug);
	}

	[Fact]
	public void LoggerShouldFilterRecordsBelowMinimumLevel()
	{
		var sink = new FakeSinkProvider();
		var provider = new LoggingHotReloadProvider(sink, LogLevel.Warning);
		var logger = provider.CreateLogger("test");

		logger.IsEnabled(LogLevel.Debug).Should().BeFalse();
		logger.IsEnabled(LogLevel.Information).Should().BeFalse();
		logger.IsEnabled(LogLevel.Warning).Should().BeTrue();
		logger.IsEnabled(LogLevel.Error).Should().BeTrue();
		logger.IsEnabled(LogLevel.Critical).Should().BeTrue();

		logger.Log(LogLevel.Information, default, "ignored", null, StaticFormatter);
		logger.Log(LogLevel.Error, default, "reported", null, StaticFormatter);

		sink.Logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Error);
	}

	[Fact]
	public void LevelChangeShouldAffectExistingLoggersImmediately()
	{
		var sink = new FakeSinkProvider();
		var provider = new LoggingHotReloadProvider(sink);
		var logger = provider.CreateLogger("test");

		logger.Log(LogLevel.Information, default, "before", null, StaticFormatter);
		sink.Logger.Entries.Should().ContainSingle();

		provider.MinimumLevel = LogLevel.Warning;
		logger.IsEnabled(LogLevel.Information).Should().BeFalse();
		logger.Log(LogLevel.Information, default, "after", null, StaticFormatter);
		logger.Log(LogLevel.Warning, default, "allowed", null, StaticFormatter);

		sink.Logger.Entries.Should().HaveCount(2);
		sink.Logger.Entries[1].Message.Should().Be("allowed");
	}

	[Fact]
	public void UnconfiguredLevelShouldDelegateFilteringToSink()
	{
		var sink = new FakeSinkProvider();
		var provider = new LoggingHotReloadProvider(sink, LogLevel.Trace)
		{
			MinimumLevel = null
		};
		var logger = provider.CreateLogger("test");

		sink.Logger.Enabled = false;
		logger.IsEnabled(LogLevel.Trace).Should().BeFalse();

		sink.Logger.Enabled = true;
		logger.IsEnabled(LogLevel.Trace).Should().BeTrue();
	}

	[Fact]
	public void WithoutSinkRecordsShouldBeDiscarded()
	{
		var provider = new LoggingHotReloadProvider();
		var logger = provider.CreateLogger("test");

		logger.IsEnabled(LogLevel.Information).Should().BeFalse();
		var act = () => logger.Log(LogLevel.Information, default, "dropped", null, StaticFormatter);

		act.Should().NotThrow();
	}

	[Fact]
	public void IsEnabledShouldReturnFalseForNone()
	{
		var sink = new FakeSinkProvider();
		var provider = new LoggingHotReloadProvider(sink, LogLevel.Trace);
		var logger = provider.CreateLogger("test");

		logger.IsEnabled(LogLevel.None).Should().BeFalse();
	}

	[Fact]
	public void CreateLoggerShouldRejectNullCategoryName()
	{
		var provider = new LoggingHotReloadProvider();

		var act = () => provider.CreateLogger(null!);

		act.Should().Throw<ArgumentNullException>();
	}

	private static string StaticFormatter(string state, Exception? exception)
	{
		return exception is null ? state : $"{state}: {exception.Message}";
	}

	private sealed class FakeSinkProvider : ILoggerProvider
	{
		public FakeSinkLogger Logger { get; } = new();

		public ILogger CreateLogger(string categoryName)
		{
			return Logger;
		}

		public void Dispose()
		{
		}
	}

	private sealed class FakeSinkLogger : ILogger
	{
		public bool Enabled { get; set; } = true;

		public List<(LogLevel Level, string Message)> Entries { get; } = new();

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull
		{
			return null;
		}

		public bool IsEnabled(LogLevel logLevel)
		{
			return Enabled && logLevel != LogLevel.None;
		}

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			Entries.Add((logLevel, formatter(state, exception)));
		}
	}
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Skynet.Core;
using Skynet.Extras;
using Xunit;

namespace Skynet.Extras.Tests;

public class DebugConsoleCommandProcessorTests
{
	[Fact]
	public async Task HelpCommandShouldListAvailableCommands()
	{
		var gateway = new FakeGateway();
		var processor = new DebugConsoleCommandProcessor(gateway);
		var result = await processor.ExecuteAsync("help", CancellationToken.None).ConfigureAwait(false);

		result.Should().NotBeNull();
		result.Response.Should().Contain("help");
		result.ShouldExit.Should().BeFalse();
	}

	[Fact]
	public async Task ListCommandShouldRenderActorTable()
	{
		var gateway = new FakeGateway();
		gateway.SeedActor(new ActorHandle(10), "echo", typeof(object));
		var processor = new DebugConsoleCommandProcessor(gateway);

		var result = await processor.ExecuteAsync("list", CancellationToken.None).ConfigureAwait(false);

		result.Response.Should().Contain("echo");
		result.ShouldExit.Should().BeFalse();
	}

	[Fact]
	public async Task InfoCommandShouldReturnDetails()
	{
		var gateway = new FakeGateway();
		gateway.SeedActor(new ActorHandle(20), "room", typeof(object));
		var processor = new DebugConsoleCommandProcessor(gateway);

		var result = await processor.ExecuteAsync("info 20", CancellationToken.None).ConfigureAwait(false);

		result.Response.Should().Contain("Handle: 20");
		result.Response.Should().Contain("Name: room");
	}

	[Fact]
	public async Task TraceCommandShouldToggleState()
	{
		var gateway = new FakeGateway();
		gateway.SeedActor(new ActorHandle(30), "trace", typeof(object));
		var processor = new DebugConsoleCommandProcessor(gateway);

		var enable = await processor.ExecuteAsync("trace 30 on", CancellationToken.None).ConfigureAwait(false);
		enable.Response.Should().Contain("enabled");
		gateway.Metrics.IsTracing(new ActorHandle(30)).Should().BeTrue();

		var disable = await processor.ExecuteAsync("trace 30 off", CancellationToken.None).ConfigureAwait(false);
		disable.Response.Should().Contain("disabled");
		gateway.Metrics.IsTracing(new ActorHandle(30)).Should().BeFalse();
	}

	[Fact]
	public async Task KillCommandShouldInvokeGateway()
	{
		var gateway = new FakeGateway();
		gateway.SeedActor(new ActorHandle(40), "kill", typeof(object));
		var processor = new DebugConsoleCommandProcessor(gateway);

		var result = await processor.ExecuteAsync("kill 40", CancellationToken.None).ConfigureAwait(false);

		result.Response.Should().Contain("terminated");
		gateway.LastKilled.Should().Be(new ActorHandle(40));
	}

	private sealed class FakeGateway : IDebugConsoleActorGateway
	{
		private readonly Dictionary<string, ActorHandle> _names = new(StringComparer.OrdinalIgnoreCase);

		public ActorMetricsCollector Metrics { get; } = new();

		public List<ActorDescriptor> Actors { get; } = new();

		public ActorHandle? LastKilled { get; private set; }

		public IReadOnlyCollection<ActorDescriptor> ListActors() => Actors;

		public bool TryResolveHandle(string identifier, out ActorHandle handle, out string? error)
		{
			if (long.TryParse(identifier, out var numeric))
			{
				var candidate = new ActorHandle(numeric);
				if (Metrics.TryGetSnapshot(candidate, out _))
				{
					handle = candidate;
					error = null;
					return true;
				}

				handle = ActorHandle.None;
				error = $"Actor {candidate.Value} is not registered.";
				return false;
			}

			if (_names.TryGetValue(identifier, out var resolved))
			{
				handle = resolved;
				error = null;
				return true;
			}

			handle = ActorHandle.None;
			error = $"Unknown actor '{identifier}'.";
			return false;
		}

		public Task<bool> KillAsync(ActorHandle handle, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			LastKilled = handle;
			return Task.FromResult(true);
		}

		public void SeedActor(ActorHandle handle, string name, Type type)
		{
				Actors.Add(new ActorDescriptor(handle, name, type));
				Metrics.RegisterActor(handle, name, type);
				Metrics.OnMessageEnqueued(handle);
				Metrics.OnMessageDequeued(handle);
				Metrics.OnMessageProcessed(handle, TimeSpan.FromMilliseconds(2), true);
				_names[name] = handle;
		}
}
}

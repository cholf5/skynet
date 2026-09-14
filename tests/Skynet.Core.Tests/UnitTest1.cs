using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Skynet.Core;
using Xunit;

namespace Skynet.Core.Tests;

public sealed class ActorSystemTests
{
	[Fact]
	public async Task CallAsync_ShouldProcessMessagesSequentially()
	{
		await using var system = new ActorSystem();
		var actor = await system.CreateActorAsync(() => new CounterActor());

		var tasks = Enumerable.Range(0, 32)
			.Select(_ => actor.CallAsync<int>(new Increment(1, TimeSpan.FromMilliseconds(1))));

		var results = await Task.WhenAll(tasks);
		results.Should().Equal(Enumerable.Range(1, 32));
	}

	[Fact]
	public async Task CallAsync_ShouldSurfaceExceptionsWithoutStoppingActor()
	{
		await using var system = new ActorSystem();
		var actor = await system.CreateActorAsync(() => new CounterActor());

		await Assert.ThrowsAsync<InvalidOperationException>(() => actor.CallAsync<int>(new Fail()));
		var value = await actor.CallAsync<int>(new Increment(1, TimeSpan.Zero));
		value.Should().Be(1);
	}

	[Fact]
	public async Task GetByName_ShouldResolveRegisteredActor()
	{
		await using var system = new ActorSystem();
		var original = await system.CreateActorAsync(() => new CounterActor(), "counter");
		var resolved = system.GetByName("counter");

		var result = await resolved.CallAsync<int>(new Increment(1, TimeSpan.Zero));
		result.Should().Be(1);
		resolved.Handle.Should().Be(original.Handle);
	}

	[Fact]
	public async Task SendAsync_ShouldDeliverFireAndForgetMessages()
	{
		await using var system = new ActorSystem();
		var actor = await system.CreateActorAsync(() => new CounterActor());

		await actor.SendAsync(new Increment(5, TimeSpan.Zero));
		var value = await actor.CallAsync<int>(new GetCount());
		value.Should().Be(5);
	}

	[Fact]
	public async Task CallAsync_ShouldWorkWhenShortCircuitIsDisabled()
	{
		var options = new InProcTransportOptions
		{
			ShortCircuitLocalDelivery = false
		};
		await using var system = new ActorSystem(inProcOptions: options);
		var actor = await system.CreateActorAsync(() => new CounterActor());

		var value = await actor.CallAsync<int>(new Increment(1, TimeSpan.Zero));
		value.Should().Be(1);
	}

	[Fact]
	public async Task UniqueService_ShouldReturnSameHandle()
	{
		await using var system = new ActorSystem();
		var first = await system.GetOrCreateUniqueAsync("unique", () => new CounterActor());
		var second = await system.GetOrCreateUniqueAsync("unique", () => new CounterActor());

		second.Handle.Should().Be(first.Handle);
	}

	[Fact]
	public async Task CallAsync_ShouldFailFastWithStartupExceptionWhenStartHookFails()
	{
		// DeliverLocalAsync no longer awaits the start hook, so a message enqueued while the start
		// hook is in flight must not hang forever when the hook fails: the host drains the mailbox
		// and fails every queued completion with the original startup exception.
		await using var system = new ActorSystem();
		var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		var createTask = system.CreateActorAsync(
			() => new FailingStartActor(startEntered, releaseStart), "fails");

		// Name registration happens before the start hook completes, so the actor is reachable
		// while its start hook is still running.
		var remote = system.GetByName("fails");
		var callTask = remote.CallAsync<string>(new Ping(), TimeSpan.FromSeconds(30));
		await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

		releaseStart.TrySetResult(); // the start hook now fails

		Func<Task> act = async () => await callTask;
		(await act.Should().ThrowAsync<InvalidOperationException>().WaitAsync(TimeSpan.FromSeconds(10)))
			.Which.Message.Should().Contain("start hook failed");
		await Assert.ThrowsAsync<InvalidOperationException>(() => createTask);
	}

	private sealed class CounterActor : Actor
	{
		private int _count;
		private bool _processing;

		protected override async Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
			if (_processing)
			{
				throw new InvalidOperationException("Reentrant processing detected.");
			}

			_processing = true;
			try
			{
				switch (envelope.Payload)
				{
					case Increment increment:
						_count += increment.Amount;
						if (increment.Delay > TimeSpan.Zero)
						{
							await Task.Delay(increment.Delay, cancellationToken);
						}
						return _count;
					case GetCount:
						return _count;
					case Fail:
						throw new InvalidOperationException("Intentional failure");
					default:
						throw new InvalidOperationException($"Unsupported payload type {envelope.Payload?.GetType().Name}.");
				}
			}
			finally
			{
				_processing = false;
			}
		}
	}

	private sealed record Increment(int Amount, TimeSpan Delay);

	private sealed record GetCount;

	private sealed class FailingStartActor : Actor
	{
		private readonly TaskCompletionSource _startEntered;
		private readonly TaskCompletionSource _releaseStart;

		public FailingStartActor(TaskCompletionSource startEntered, TaskCompletionSource releaseStart)
		{
			_startEntered = startEntered;
			_releaseStart = releaseStart;
		}

		protected override async ValueTask HandleStartAsync(CancellationToken cancellationToken)
		{
			_startEntered.TrySetResult();
			await _releaseStart.Task.WaitAsync(cancellationToken);
			throw new InvalidOperationException("start hook failed");
		}

		protected override Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
			return Task.FromResult<object?>(null);
		}
	}

	private sealed record Fail;

	private sealed record Ping;
}

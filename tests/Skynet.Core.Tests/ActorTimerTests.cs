using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Skynet.Core;
using Xunit;

namespace Skynet.Core.Tests;

public sealed class ActorTimerTests
{
	[Fact]
	public async Task TimerCallback_ShouldRunSerializedWithMessages()
	{
		var events = new ConcurrentQueue<string>();
		var errors = new ConcurrentQueue<string>();
		await using var system = new ActorSystem();
		var actor = await system.CreateActorAsync(() => new TimerTestActor(events, errors));

		// Timer fires in ~50ms, then runs for 300ms inside the actor loop.
		await actor.CallAsync<long>(new ArmOneShot("busy", DelayMs: 50, WorkMs: 300));
		// Concurrently queue five ordinary messages that also occupy the actor.
		var messages = Enumerable.Range(0, 5)
			.Select(i => actor.CallAsync<bool>(new SlowWork($"m{i}", WorkMs: 30)));
		await Task.WhenAll(messages);

		await WaitForAsync(() => events.Count >= 6, TimeSpan.FromSeconds(10));

		errors.Should().BeEmpty("timer callbacks and messages must never overlap inside the actor");
		events.Should().Contain("timer:busy");
		events.Count(e => e.StartsWith("msg:", StringComparison.Ordinal)).Should().Be(5);
	}

	[Fact]
	public async Task OneShotTimer_ShouldFireExactlyOnce()
	{
		var events = new ConcurrentQueue<string>();
		var errors = new ConcurrentQueue<string>();
		await using var system = new ActorSystem();
		var actor = await system.CreateActorAsync(() => new TimerTestActor(events, errors));

		var timerId = await actor.CallAsync<long>(new ArmOneShot("once", DelayMs: 30, WorkMs: 0));
		timerId.Should().BePositive();

		await WaitForAsync(() => events.Count >= 1, TimeSpan.FromSeconds(5));
		await Task.Delay(150);

		events.Count(e => e == "timer:once").Should().Be(1, "a one-shot timer must fire exactly once");
		// Already fired, so cancellation must report false.
		(await actor.CallAsync<bool>(new CancelTimerCmd(timerId))).Should().BeFalse();
	}

	[Fact]
	public async Task CancelTimer_ShouldPreventCallback_AndReportState()
	{
		var events = new ConcurrentQueue<string>();
		var errors = new ConcurrentQueue<string>();
		await using var system = new ActorSystem();
		var actor = await system.CreateActorAsync(() => new TimerTestActor(events, errors));

		var timerId = await actor.CallAsync<long>(new ArmOneShot("gone", DelayMs: 300, WorkMs: 0));
		(await actor.CallAsync<bool>(new CancelTimerCmd(timerId))).Should().BeTrue();
		system.Timers.ActiveTimerCount.Should().Be(0, "cancelling a timer must remove it from the scheduler");
		(await actor.CallAsync<bool>(new CancelTimerCmd(timerId))).Should().BeFalse("a second cancel must not find the timer");

		await Task.Delay(500);
		events.Should().BeEmpty("a cancelled timer must never fire");
	}

	[Fact]
	public async Task PeriodicTimer_ShouldFireRepeatedly_AndStopAfterCancel()
	{
		var events = new ConcurrentQueue<string>();
		var errors = new ConcurrentQueue<string>();
		await using var system = new ActorSystem();
		var actor = await system.CreateActorAsync(() => new TimerTestActor(events, errors));

		var timerId = await actor.CallAsync<long>(new ArmPeriodic("tick", IntervalMs: 40));
		await WaitForAsync(() => events.Count >= 4, TimeSpan.FromSeconds(5));

		(await actor.CallAsync<bool>(new CancelTimerCmd(timerId))).Should().BeTrue();
		await Task.Delay(300);
		var countAfterCancel = events.Count;
		await Task.Delay(300);
		var countLater = events.Count;

		countLater.Should().Be(countAfterCancel, "no ticks may arrive after cancellation");
		(countAfterCancel - countLater).Should().Be(0);
		// At most one in-flight tick that was dispatched just before cancellation.
		countAfterCancel.Should().BeLessThanOrEqualTo(6);
		errors.Should().BeEmpty();
	}

	[Fact]
	public async Task TimerCallbackException_ShouldNotKillActor()
	{
		var events = new ConcurrentQueue<string>();
		var errors = new ConcurrentQueue<string>();
		await using var system = new ActorSystem();
		var actor = await system.CreateActorAsync(() => new TimerTestActor(events, errors));

		await actor.CallAsync<bool>(new ArmFailingOneShot("boom", DelayMs: 20));
		await WaitForAsync(() => !errors.IsEmpty, TimeSpan.FromSeconds(5));

		// The actor must still process messages after the failing callback.
		var response = await actor.CallAsync<bool>(new SlowWork("after", WorkMs: 0));
		response.Should().BeTrue();
		events.Should().Contain("msg:after");
	}

	[Fact]
	public async Task KillActor_ShouldCancelTimers_AndClearScheduler()
	{
		var events = new ConcurrentQueue<string>();
		var errors = new ConcurrentQueue<string>();
		await using var system = new ActorSystem();
		var actor = await system.CreateActorAsync(() => new TimerTestActor(events, errors));

		await actor.CallAsync<long>(new ArmPeriodic("loop", IntervalMs: 50));
		await actor.CallAsync<long>(new ArmOneShot("late", DelayMs: 500, WorkMs: 0));
		system.Timers.ActiveTimerCount.Should().Be(2);

		(await system.KillAsync(actor.Handle)).Should().BeTrue();
		system.Timers.ActiveTimerCount.Should().Be(0, "killing an actor must clear all of its timers");

		await Task.Delay(400);
		events.Should().BeEmpty("timers of a killed actor must never fire");
	}

	[Fact]
	public async Task Stress_TenThousandTimers_ShouldRegisterFireAndCancel()
	{
		var fired = 0;
		var events = new ConcurrentQueue<string>();
		var errors = new ConcurrentQueue<string>();
		await using var system = new ActorSystem();
		var actor = await system.CreateActorAsync(() => new TimerTestActor(events, errors, onTimerFired: () => Interlocked.Increment(ref fired)));

		// Phase 1: register 10k one-shot timers and let all of them fire.
		var stopwatch = Stopwatch.StartNew();
		await actor.CallAsync<bool>(new ArmManyOneShots(Count: 10_000, MinDelayMs: 1, MaxDelayMs: 150));
		stopwatch.Stop();
		stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "registering 10k timers must be fast");

		await WaitForAsync(() => Volatile.Read(ref fired) >= 10_000, TimeSpan.FromSeconds(30));

		// Phase 2: register 10k far-future timers and cancel every one of them.
		await actor.CallAsync<bool>(new ArmManyOneShots(Count: 10_000, MinDelayMs: 600_000, MaxDelayMs: 600_000));
		system.Timers.ActiveTimerCount.Should().Be(10_000);
		var cancelled = await actor.CallAsync<int>(new CancelStoredTimers());
		cancelled.Should().Be(10_000);
		system.Timers.ActiveTimerCount.Should().Be(0, "cancelled timers must be removed from the scheduler index");
	}

	private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
	{
		var stopwatch = Stopwatch.StartNew();
		while (!condition())
		{
			if (stopwatch.Elapsed > timeout)
			{
				throw new TimeoutException("The awaited condition was not met within the timeout.");
			}

			await Task.Delay(10).ConfigureAwait(false);
		}
	}

	private sealed class TimerTestActor : Actor
	{
		private readonly ConcurrentQueue<string> _events;
		private readonly ConcurrentQueue<string> _errors;
		private readonly Action? _onTimerFired;
		private readonly List<long> _storedTimers = new();
		private int _busy;

		public TimerTestActor(ConcurrentQueue<string> events, ConcurrentQueue<string> errors, Action? onTimerFired = null)
		{
			_events = events;
			_errors = errors;
			_onTimerFired = onTimerFired;
		}

		protected override ValueTask HandleErrorAsync(MessageEnvelope envelope, Exception exception, CancellationToken cancellationToken)
		{
			_errors.Enqueue(exception.Message);
			return ValueTask.CompletedTask;
		}

		protected override async Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
			switch (envelope.Payload)
			{
				case ArmOneShot arm:
					return AddTimer(TimeSpan.FromMilliseconds(arm.DelayMs), token => RunGuardedAsync($"timer:{arm.Tag}", arm.WorkMs, token)).Id;
				case ArmFailingOneShot fail:
					AddTimer(TimeSpan.FromMilliseconds(fail.DelayMs), _ => throw new InvalidOperationException("Intentional timer failure"));
					return true;
				case ArmPeriodic periodic:
					return SchedulePeriodic(TimeSpan.FromMilliseconds(periodic.IntervalMs), token => RunGuardedAsync($"tick:{periodic.Tag}", 0, token)).Id;
				case ArmManyOneShots many:
					return ArmManyOneShotsUnsafe(many);
				case CancelStoredTimers:
					return CancelStoredTimersUnsafe();
				case CancelTimerCmd cancel:
					return CancelTimer(new TimerHandle(cancel.TimerId));
				case SlowWork slow:
					await RunGuardedAsync($"msg:{slow.Tag}", slow.WorkMs, cancellationToken).ConfigureAwait(false);
					return true;
				default:
					throw new InvalidOperationException($"Unsupported payload type {envelope.Payload?.GetType().Name}.");
			}
		}

		private object ArmManyOneShotsUnsafe(ArmManyOneShots many)
		{
			for (var i = 0; i < many.Count; i++)
			{
				var delay = many.MinDelayMs + (i % (many.MaxDelayMs - many.MinDelayMs + 1));
				var handle = AddTimer(TimeSpan.FromMilliseconds(delay), _ =>
				{
					_onTimerFired?.Invoke();
					return ValueTask.CompletedTask;
				});
				_storedTimers.Add(handle.Id);
			}

			return true;
		}

		private object CancelStoredTimersUnsafe()
		{
			var cancelled = 0;
			foreach (var id in _storedTimers)
			{
				if (CancelTimer(new TimerHandle(id)))
				{
					cancelled++;
				}
			}

			_storedTimers.Clear();
			return cancelled;
		}

		private async ValueTask RunGuardedAsync(string tag, int workMs, CancellationToken cancellationToken)
		{
			if (Interlocked.Exchange(ref _busy, 1) != 0)
			{
				throw new InvalidOperationException($"Reentrancy detected while running {tag}.");
			}

			try
			{
				if (workMs > 0)
				{
					await Task.Delay(workMs, cancellationToken).ConfigureAwait(false);
				}

				_events.Enqueue(tag);
			}
			finally
			{
				Volatile.Write(ref _busy, 0);
			}
		}
	}

	private sealed record ArmOneShot(string Tag, int DelayMs, int WorkMs);

	private sealed record ArmFailingOneShot(string Tag, int DelayMs);

	private sealed record ArmPeriodic(string Tag, int IntervalMs);

	private sealed record ArmManyOneShots(int Count, int MinDelayMs, int MaxDelayMs);

	private sealed record CancelStoredTimers;

	private sealed record CancelTimerCmd(long TimerId);

	private sealed record SlowWork(string Tag, int WorkMs);
}

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Skynet.Core;
using Xunit;

namespace Skynet.Core.Tests;

public class ActorMetricsCollectorTests
{
	[Fact]
	public void SnapshotReflectsProcessingData()
	{
		var collector = new ActorMetricsCollector();
		var handle = new ActorHandle(1);
		collector.RegisterActor(handle, "echo", typeof(TestActor));
		collector.OnMessageEnqueued(handle);
		collector.OnMessageDequeued(handle);
		collector.OnMessageProcessed(handle, TimeSpan.FromMilliseconds(10), true);
		collector.OnMessageProcessed(handle, TimeSpan.FromMilliseconds(5), false);

		collector.TryGetSnapshot(handle, out var snapshot).Should().BeTrue();
		snapshot.QueueLength.Should().Be(0);
		snapshot.ProcessedCount.Should().Be(2);
		snapshot.ExceptionCount.Should().Be(1);
		snapshot.AverageProcessingTime.TotalMilliseconds.Should().BeApproximately(7.5, 0.1);
	}

	[Fact]
	public void TraceToggleChangesState()
	{
		var collector = new ActorMetricsCollector();
		var handle = new ActorHandle(2);
		collector.RegisterActor(handle, null, typeof(TestActor));
		collector.IsTracing(handle).Should().BeFalse();
		collector.EnableTrace(handle).Should().BeTrue();
		collector.IsTracing(handle).Should().BeTrue();
		collector.EnableTrace(handle).Should().BeFalse();
		collector.DisableTrace(handle).Should().BeTrue();
		collector.IsTracing(handle).Should().BeFalse();
	}

	[Fact]
	public void UnknownHandleOperationsAreNoOp()
	{
		var collector = new ActorMetricsCollector();
		var handle = new ActorHandle(3);
		collector.OnMessageEnqueued(handle);
		collector.OnMessageDequeued(handle);
		collector.OnMessageProcessed(handle, TimeSpan.FromMilliseconds(1), true);
		collector.DisableTrace(handle).Should().BeFalse();
	}

	private sealed class TestActor : Actor
	{
		protected override Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
				return Task.FromResult<object?>(null);
		}
	}
}

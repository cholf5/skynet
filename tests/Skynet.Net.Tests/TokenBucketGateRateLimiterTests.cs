using System.Net;
using FluentAssertions;
using Xunit;

namespace Skynet.Net.Tests;

public sealed class TokenBucketGateRateLimiterTests
{
	private static SessionMetadata Metadata(string sessionId, string ip, int port = 40000)
	{
		return new SessionMetadata(sessionId, "tcp", new IPEndPoint(IPAddress.Parse(ip), port), DateTimeOffset.UtcNow);
	}

	[Fact]
	public void Constructor_ShouldRejectInvalidSettings()
	{
		var create = (int cap, double fps, int burst) => new TokenBucketGateRateLimiter(cap, fps, burst);

		var act1 = () => create(0, 10, 4);
		var act2 = () => create(4, 0, 4);
		var act3 = () => create(4, -1, 4);
		var act4 = () => create(4, 10, 0);
		var act5 = () => create(4, 10, 1 << 22);

		act1.Should().Throw<ArgumentOutOfRangeException>();
		act2.Should().Throw<ArgumentOutOfRangeException>();
		act3.Should().Throw<ArgumentOutOfRangeException>();
		act4.Should().Throw<ArgumentOutOfRangeException>();
		act5.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public void InboundFrames_ShouldAllowBurstThenDrop()
	{
		var limiter = new TokenBucketGateRateLimiter(maxConnectionsPerIp: 8, inboundFramesPerSecond: 1, inboundFrameBurst: 2);
		var metadata = Metadata("s1", "10.0.0.1");
		limiter.EvaluateConnection(metadata).Should().Be(GateRateLimitDecision.Allow);

		limiter.EvaluateInboundFrame(metadata, 10).Should().Be(GateRateLimitDecision.Allow);
		limiter.EvaluateInboundFrame(metadata, 10).Should().Be(GateRateLimitDecision.Allow);
		limiter.EvaluateInboundFrame(metadata, 10).Should().Be(GateRateLimitDecision.Drop);
	}

	[Fact]
	public async Task InboundFrames_ShouldRefillOverTime()
	{
		var limiter = new TokenBucketGateRateLimiter(maxConnectionsPerIp: 8, inboundFramesPerSecond: 1000, inboundFrameBurst: 1);
		var metadata = Metadata("s1", "10.0.0.1");
		limiter.EvaluateConnection(metadata).Should().Be(GateRateLimitDecision.Allow);

		limiter.EvaluateInboundFrame(metadata, 10).Should().Be(GateRateLimitDecision.Allow);
		limiter.EvaluateInboundFrame(metadata, 10).Should().Be(GateRateLimitDecision.Drop);

		// 1000 fps refills a token in 1 ms; 50 ms leaves a large safety margin.
		await Task.Delay(50).ConfigureAwait(false);
		limiter.EvaluateInboundFrame(metadata, 10).Should().Be(GateRateLimitDecision.Allow);
	}

	[Fact]
	public void Connections_ShouldCapPerIpAddress()
	{
		var limiter = new TokenBucketGateRateLimiter(maxConnectionsPerIp: 1, inboundFramesPerSecond: 10, inboundFrameBurst: 4);
		var first = Metadata("s1", "10.0.0.1");
		var second = Metadata("s2", "10.0.0.1");
		var otherIp = Metadata("s3", "10.0.0.2");

		limiter.EvaluateConnection(first).Should().Be(GateRateLimitDecision.Allow);
		limiter.EvaluateConnection(second).Should().Be(GateRateLimitDecision.Close);
		limiter.EvaluateConnection(otherIp).Should().Be(GateRateLimitDecision.Allow);
	}

	[Fact]
	public void OnSessionClosed_ShouldReleaseConnectionSlotAndBucket()
	{
		var limiter = new TokenBucketGateRateLimiter(maxConnectionsPerIp: 1, inboundFramesPerSecond: 1, inboundFrameBurst: 1);
		var metadata = Metadata("s1", "10.0.0.1");

		limiter.EvaluateConnection(metadata).Should().Be(GateRateLimitDecision.Allow);
		limiter.EvaluateInboundFrame(metadata, 10).Should().Be(GateRateLimitDecision.Allow);
		limiter.TrackedSessionCount.Should().Be(1);
		limiter.TrackedConnectionCount.Should().Be(1);

		limiter.OnSessionClosed(metadata.SessionId);

		limiter.TrackedSessionCount.Should().Be(0);
		limiter.TrackedConnectionCount.Should().Be(0);

		// The freed slot allows a new connection from the same IP with a fresh bucket.
		var replacement = Metadata("s2", "10.0.0.1");
		limiter.EvaluateConnection(replacement).Should().Be(GateRateLimitDecision.Allow);
		limiter.EvaluateInboundFrame(replacement, 10).Should().Be(GateRateLimitDecision.Allow);
	}

	[Fact]
	public void OnSessionClosed_ForUnknownSession_ShouldBeIgnored()
	{
		var limiter = new TokenBucketGateRateLimiter(4, 10, 4);
		var act = () => limiter.OnSessionClosed("does-not-exist");
		act.Should().NotThrow();
	}

	[Fact]
	public void EndPointWithoutIpAddress_ShouldFallBackToStableKey()
	{
		var limiter = new TokenBucketGateRateLimiter(maxConnectionsPerIp: 1, inboundFramesPerSecond: 10, inboundFrameBurst: 4);

		var first = new SessionMetadata("s1", "tcp", null, DateTimeOffset.UtcNow);
		var second = new SessionMetadata("s2", "tcp", null, DateTimeOffset.UtcNow);

		limiter.EvaluateConnection(first).Should().Be(GateRateLimitDecision.Allow);
		limiter.EvaluateConnection(second).Should().Be(GateRateLimitDecision.Close);
	}
}

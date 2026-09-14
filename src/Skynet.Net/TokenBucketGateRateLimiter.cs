using System.Collections.Concurrent;
using System.Net;

namespace Skynet.Net;

/// <summary>
/// Default <see cref="IGateRateLimiter"/> combining a per-remote-IP concurrent connection cap with a
/// per-session token bucket for inbound frames. Buckets refill lazily from elapsed time, so the limiter
/// runs no background timers. Frame exhaustion yields <see cref="GateRateLimitDecision.Drop"/>; callers
/// that want to terminate abusive sessions can implement <see cref="IGateRateLimiter"/> themselves.
/// </summary>
public sealed class TokenBucketGateRateLimiter : IGateRateLimiter
{
	private const int TokenBits = 21;
	private const long TokenMask = (1L << TokenBits) - 1;
	private const int MaxBurst = (1 << TokenBits) - 1;

	private readonly ConcurrentDictionary<string, int> _connectionsByIp = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, string> _sessionIpKeys = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new(StringComparer.Ordinal);
	private readonly int _maxConnectionsPerIp;
	private readonly double _framesPerSecond;
	private readonly int _frameBurst;

	/// <summary>
	/// Creates a limiter.
	/// </summary>
	/// <param name="maxConnectionsPerIp">Maximum concurrent connections admitted per remote IP address.</param>
	/// <param name="inboundFramesPerSecond">Steady-state inbound frames per second and per session.</param>
	/// <param name="inboundFrameBurst">Maximum burst of inbound frames per session before throttling.</param>
	public TokenBucketGateRateLimiter(int maxConnectionsPerIp, double inboundFramesPerSecond, int inboundFrameBurst)
	{
		if (maxConnectionsPerIp < 1)
		{
			throw new ArgumentOutOfRangeException(nameof(maxConnectionsPerIp), "At least one connection per IP must be allowed.");
		}

		if (inboundFramesPerSecond <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(inboundFramesPerSecond), "Frame rate must be positive.");
		}

		if (inboundFrameBurst < 1 || inboundFrameBurst > MaxBurst)
		{
			throw new ArgumentOutOfRangeException(nameof(inboundFrameBurst), $"Burst must be between 1 and {MaxBurst}.");
		}

		_maxConnectionsPerIp = maxConnectionsPerIp;
		_framesPerSecond = inboundFramesPerSecond;
		_frameBurst = inboundFrameBurst;
	}

	/// <summary>Gets the number of sessions currently tracked by the frame bucket table.</summary>
	public int TrackedSessionCount => _buckets.Count;

	/// <summary>Gets the number of sessions currently holding a per-IP admission slot.</summary>
	public int TrackedConnectionCount => _sessionIpKeys.Count;

	/// <inheritdoc />
	public GateRateLimitDecision EvaluateConnection(SessionMetadata metadata)
	{
		var ipKey = GetIpKey(metadata.RemoteEndPoint);
		var current = _connectionsByIp.AddOrUpdate(ipKey, 1, static (_, existing) => existing + 1);
		if (current <= _maxConnectionsPerIp)
		{
			_sessionIpKeys[metadata.SessionId] = ipKey;
			return GateRateLimitDecision.Allow;
		}

		Decrement(_connectionsByIp, ipKey);
		return GateRateLimitDecision.Close;
	}

	/// <inheritdoc />
	public GateRateLimitDecision EvaluateInboundFrame(SessionMetadata metadata, int payloadSize)
	{
		var bucket = _buckets.GetOrAdd(
			metadata.SessionId,
			static (_, state) => new TokenBucket(state.Burst),
			(Fps: _framesPerSecond, Burst: _frameBurst));
		return bucket.TryConsume(_framesPerSecond, _frameBurst)
			? GateRateLimitDecision.Allow
			: GateRateLimitDecision.Drop;
	}

	/// <inheritdoc />
	public void OnSessionClosed(string sessionId)
	{
		_buckets.TryRemove(sessionId, out _);
		if (_sessionIpKeys.TryRemove(sessionId, out var ipKey))
		{
			Decrement(_connectionsByIp, ipKey);
		}
	}

	private static string GetIpKey(EndPoint? remoteEndPoint)
	{
		return remoteEndPoint switch
		{
			IPEndPoint ip => ip.Address.ToString(),
			null => "unknown",
			_ => remoteEndPoint.ToString() ?? "unknown"
		};
	}

	private static void Decrement(ConcurrentDictionary<string, int> dictionary, string key)
	{
		while (true)
		{
			if (!dictionary.TryGetValue(key, out var value))
			{
				return;
			}

			if (value <= 1)
			{
				if (((ICollection<KeyValuePair<string, int>>)dictionary).Remove(new KeyValuePair<string, int>(key, value)))
				{
					return;
				}
			}
			else if (dictionary.TryUpdate(key, value - 1, value))
			{
				return;
			}
		}
	}

	/// <summary>
	/// Lock-free token bucket. State packs the refill timestamp (milliseconds since boot) in the upper
	/// bits and the whole-token count in the lower <see cref="TokenBits"/> bits, so a single CAS covers
	/// both fields. On rejection the last refill timestamp is preserved so partial credit keeps accruing.
	/// </summary>
	private sealed class TokenBucket(long initialTokens)
	{
		private long _state = (Environment.TickCount64 << TokenBits) | initialTokens;

		public bool TryConsume(double framesPerSecond, int burst)
		{
			var now = Environment.TickCount64;
			while (true)
			{
				var packed = Volatile.Read(ref _state);
				var tokens = packed & TokenMask;
				var lastRefill = packed >> TokenBits;
				var elapsed = Math.Max(0, now - lastRefill);
				var accrued = tokens + (long)(elapsed * framesPerSecond / 1000.0);
				if (accrued < 1)
				{
					return false;
				}

				var newTokens = Math.Min(burst, accrued) - 1;
				var updated = (now << TokenBits) | newTokens;
				if (Interlocked.CompareExchange(ref _state, updated, packed) == packed)
				{
					return true;
				}
			}
		}
	}
}

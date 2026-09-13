namespace Skynet.Net;

/// <summary>
/// Exponential backoff policy for outbound Gate reconnect attempts. <see cref="TryGetDelay"/>
/// decides whether another attempt may be made and how long to wait before it.
/// </summary>
public sealed class ReconnectPolicy
{
	public ReconnectPolicy(int maxAttempts = 3, TimeSpan? initialDelay = null, TimeSpan? maxDelay = null,
		double backoffFactor = 2)
	{
		if (maxAttempts < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "Max attempts cannot be negative.");
		}

		if (backoffFactor < 1)
		{
			throw new ArgumentOutOfRangeException(nameof(backoffFactor), backoffFactor, "Backoff factor must be at least 1.");
		}

		MaxAttempts = maxAttempts;
		InitialDelay = initialDelay ?? TimeSpan.FromSeconds(1);
		MaxDelay = maxDelay ?? TimeSpan.FromSeconds(30);
		BackoffFactor = backoffFactor;

		if (InitialDelay < TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(initialDelay), InitialDelay, "Initial delay cannot be negative.");
		}

		if (MaxDelay < InitialDelay)
		{
			throw new ArgumentOutOfRangeException(nameof(maxDelay), MaxDelay, "Max delay cannot be smaller than initial delay.");
		}
	}

	/// <summary>Maximum number of reconnect attempts per connection loss. 0 means a single attempt and no retries.</summary>
	public int MaxAttempts { get; }

	public TimeSpan InitialDelay { get; }

	public TimeSpan MaxDelay { get; }

	public double BackoffFactor { get; }

	/// <summary>
	/// Computes the delay before retry number <paramref name="attempt"/>. Returns false when the
	/// retry budget is exhausted and the client should settle in the Disconnected state.
	/// </summary>
	public bool TryGetDelay(int attempt, out TimeSpan delay)
	{
		if (attempt < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "Attempt cannot be negative.");
		}

		if (attempt >= MaxAttempts)
		{
			delay = TimeSpan.Zero;
			return false;
		}

		var factor = Math.Pow(BackoffFactor, attempt);
		var ticks = (long)Math.Min(MaxDelay.Ticks, InitialDelay.Ticks * factor);
		delay = TimeSpan.FromTicks(ticks);
		return true;
	}
}

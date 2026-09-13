namespace Skynet.Net;

/// <summary>
/// Waits out a reconnect backoff delay. Injectable so tests can replace real time with
/// blocking, recording, or immediate strategies.
/// </summary>
public interface IReconnectDelayStrategy
{
	ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}

/// <summary>Default strategy backed by <see cref="Task.Delay"/>.</summary>
public sealed class TimerReconnectDelayStrategy : IReconnectDelayStrategy
{
	public static TimerReconnectDelayStrategy Instance { get; } = new();

	private TimerReconnectDelayStrategy()
	{
	}

	public async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
	{
		if (delay <= TimeSpan.Zero)
		{
			return;
		}

		await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
	}
}

/// <summary>Skips reconnect delays entirely; useful for tests and eager reconnect loops.</summary>
public sealed class NoopReconnectDelayStrategy : IReconnectDelayStrategy
{
	public static NoopReconnectDelayStrategy Instance { get; } = new();

	private NoopReconnectDelayStrategy()
	{
	}

	public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
	{
		_ = delay;
		_ = cancellationToken;
		return ValueTask.CompletedTask;
	}
}

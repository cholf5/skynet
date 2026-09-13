namespace Skynet.Core;

/// <summary>
/// Identifies a timer registered through <see cref="Actor.AddTimer"/> or <see cref="Actor.SchedulePeriodic"/>.
/// Handles are opaque and unique within a single <see cref="ActorSystem"/>.
/// </summary>
public readonly record struct TimerHandle(long Id)
{
	/// <summary>
	/// Gets an invalid timer handle.
	/// </summary>
	public static TimerHandle None => default;

	/// <summary>
	/// Gets a value indicating whether the handle references a live or previously registered timer.
	/// </summary>
	public bool IsValid => Id != 0;

	/// <inheritdoc />
	public override string ToString() => IsValid ? $"TimerHandle({Id})" : "TimerHandle(None)";
}

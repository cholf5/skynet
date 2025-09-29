namespace Skynet.Core;

/// <summary>
/// Represents the unique identifier of an actor inside a single <see cref="ActorSystem"/>.
/// </summary>
public readonly record struct ActorHandle(long Value)
{
	/// <summary>
	/// Gets an invalid <see cref="ActorHandle"/>.
	/// </summary>
	public static readonly ActorHandle None = new(0);

	/// <summary>
	/// Gets a value indicating whether the handle references a valid actor.
	/// </summary>
	public bool IsValid => Value != 0;

	/// <inheritdoc />
	public override string ToString()
	{
		return IsValid ? $"ActorHandle({Value})" : "ActorHandle(None)";
	}

	/// <summary>
	/// Implicitly converts the <see cref="ActorHandle"/> to its integral representation.
	/// </summary>
	/// <param name="handle">The actor handle to convert.</param>
	public static implicit operator long(ActorHandle handle) => handle.Value;
}

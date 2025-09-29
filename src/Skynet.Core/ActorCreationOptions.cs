namespace Skynet.Core;

/// <summary>
/// Provides optional settings for actor creation.
/// </summary>
public sealed class ActorCreationOptions
{
	/// <summary>
	/// Gets or sets the explicit handle assigned to the actor.
	/// </summary>
	public ActorHandle? HandleOverride { get; init; }
}

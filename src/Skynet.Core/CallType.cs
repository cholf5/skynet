namespace Skynet.Core;

/// <summary>
/// Describes the semantics of a message being sent through the actor runtime.
/// </summary>
public enum CallType
{
	/// <summary>
	/// Fire-and-forget message delivery.
	/// </summary>
	Send = 1,

	/// <summary>
	/// Request-response invocation that expects a reply.
	/// </summary>
	Call = 2
}

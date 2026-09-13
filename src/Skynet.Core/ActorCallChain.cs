namespace Skynet.Core;

/// <summary>
/// Represents the immutable chain of actor handles whose handlers are currently suspended in the
/// causal call stack, ordered from the outermost caller to the actor processing the current
/// message. The chain only accumulates blocking (call) edges: a fire-and-forget send breaks the
/// causal chain and never joins it.
/// </summary>
/// <remarks>
/// The chain is used to detect cyclical call graphs (A calling B calling A). Because every actor
/// mailbox is strictly serial, a cycle would suspend all participating mailboxes forever; the
/// framework therefore fails the call loudly with an <see cref="ActorCallCycleException"/> instead
/// of letting it hang. The chain flows across actor boundaries through
/// <see cref="MessageEnvelope.CallChain"/> (in-process only) and within an actor's message
/// processing through an <c>AsyncLocal</c> ambient scope.
/// </remarks>
public sealed class ActorCallChain
{
	private ActorCallChain(ActorCallChain? previous, ActorHandle handle)
	{
		_previous = previous;
		Handle = handle;
		Depth = (previous?.Depth ?? 0) + 1;
	}

	private readonly ActorCallChain? _previous;

	/// <summary>
	/// Gets the handle of the actor that most recently joined the chain.
	/// </summary>
	public ActorHandle Handle { get; }

	/// <summary>
	/// Gets the number of actors currently in the chain.
	/// </summary>
	public int Depth { get; }

	internal static ActorCallChain Push(ActorCallChain? chain, ActorHandle handle) => new(chain, handle);

	/// <summary>
	/// Determines whether the specified actor handle already appears in the chain.
	/// </summary>
	/// <param name="handle">The handle to look for.</param>
	/// <returns><see langword="true"/> when the handle is part of the chain.</returns>
	public bool Contains(ActorHandle handle)
	{
		for (var node = this; node is not null; node = node._previous)
		{
			if (node.Handle == handle)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Formats the call path from the outermost actor in the chain to <paramref name="target"/>,
	/// for example <c>1 → 2 → 1</c>. Actor names are appended in parentheses when
	/// <paramref name="resolveName"/> supplies them.
	/// </summary>
	/// <param name="target">The handle that closes the cycle; it is appended after the chain.</param>
	/// <param name="resolveName">Optional resolver that maps a handle to its logical name.</param>
	/// <returns>The formatted cycle path.</returns>
	public string FormatPath(ActorHandle target, Func<ActorHandle, string?>? resolveName = null)
	{
		var handles = new List<ActorHandle>(Depth + 1);
		for (var node = this; node is not null; node = node._previous)
		{
			handles.Add(node.Handle);
		}

		handles.Reverse();
		handles.Add(target);

		return string.Join(" → ", handles.Select(handle =>
		{
			var name = resolveName?.Invoke(handle);
			return name is null ? handle.Value.ToString() : $"{handle.Value}({name})";
		}));
	}
}

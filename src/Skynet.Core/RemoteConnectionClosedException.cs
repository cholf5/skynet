namespace Skynet.Core;

/// <summary>
/// Represents the failure of a pending remote call because the connection to the remote node was
/// closed before a response was received.
/// </summary>
public sealed class RemoteConnectionClosedException : IOException
{
	/// <summary>
	/// Gets the identifier of the node whose connection was closed, if known.
	/// </summary>
	public string? NodeId
	{
		get;
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="RemoteConnectionClosedException"/> class.
	/// </summary>
	/// <param name="nodeId">The identifier of the remote node, if known.</param>
	/// <param name="message">An optional custom message.</param>
	/// <param name="innerException">The underlying exception, if any.</param>
	public RemoteConnectionClosedException(string? nodeId, string? message = null, Exception? innerException = null)
		: base(message ?? $"The connection to node '{nodeId ?? "<unknown>"}' was closed before the call completed.",
			innerException)
	{
		NodeId = nodeId;
	}
}

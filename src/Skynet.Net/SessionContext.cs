using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;
using Skynet.Core;

namespace Skynet.Net;

/// <summary>
/// Provides contextual information for the lifetime of a client session.
/// </summary>
public sealed class SessionContext
{
	private readonly ISessionConnection _connection;
	private readonly ILogger _logger;
	private readonly ConcurrentDictionary<string, object?> _items = new(StringComparer.Ordinal);
	private bool _isClosed;

	internal SessionContext(
		ActorSystem system,
		ActorHandle sessionHandle,
		ISessionConnection connection,
		SessionMetadata metadata,
		ILogger logger)
	{
		System = system;
		SessionHandle = sessionHandle;
		_connection = connection;
		Metadata = metadata;
		_logger = logger;
	}

	/// <summary>
	/// Gets the actor system hosting the session.
	/// </summary>
	public ActorSystem System { get; }

	/// <summary>
	/// Gets the handle of the session actor.
	/// </summary>
	public ActorHandle SessionHandle { get; }

	/// <summary>
	/// Gets metadata captured when the client connected.
	/// </summary>
	public SessionMetadata Metadata { get; }

	/// <summary>
	/// Gets an extensible bag for application specific state.
	/// </summary>
	public IDictionary<string, object?> Items => _items;

	/// <summary>
	/// Gets or sets the actor handle bound to this session for direct routing.
	/// </summary>
	public ActorHandle? BoundActor { get; private set; }

	/// <summary>
	/// Gets a value indicating whether the session has been closed.
	/// </summary>
	public bool IsClosed => _isClosed;

	internal ILogger Logger => _logger;

	/// <summary>
	/// Binds the session to a specific actor handle for subsequent routing convenience.
	/// </summary>
	public void BindActor(ActorHandle handle)
	{
		BoundActor = handle;
	}

	/// <summary>
	/// Sends a binary payload to the connected client.
	/// </summary>
	public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
	{
		return _connection.SendAsync(payload, cancellationToken);
	}

	/// <summary>
	/// Sends a UTF-8 encoded text payload to the connected client.
	/// </summary>
	public ValueTask SendAsync(string text, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(text);
		return _connection.SendAsync(Encoding.UTF8.GetBytes(text).AsMemory(), cancellationToken);
	}

	/// <summary>
	/// Forwards a fire-and-forget message to another actor while preserving the session as the sender.
	/// </summary>
	public ValueTask ForwardAsync(ActorHandle target, object payload, CancellationToken cancellationToken = default)
	{
		return System.SendAsync(target, payload, SessionHandle, cancellationToken);
	}

	/// <summary>
	/// Performs a request-response invocation against another actor on behalf of the session.
	/// </summary>
	public Task<TResponse> CallAsync<TResponse>(ActorHandle target, object payload, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
	{
		return System.CallAsync<TResponse>(target, payload, timeout, SessionHandle, cancellationToken);
	}

	internal void MarkClosed()
	{
		_isClosed = true;
	}
}

/// <summary>
/// Captures immutable metadata about a session connection.
/// </summary>
public sealed record SessionMetadata(
	string SessionId,
	string Protocol,
	System.Net.EndPoint? RemoteEndPoint,
	DateTimeOffset ConnectedAt);

/// <summary>
/// Defines hooks for reacting to session lifecycle events.
/// </summary>
public interface ISessionMessageRouter
{
	/// <summary>
	/// Called when the session actor has started.
	/// </summary>
	Task OnSessionStartedAsync(SessionContext context, CancellationToken cancellationToken);

	/// <summary>
	/// Called when a message from the remote client arrives.
	/// </summary>
	Task OnSessionMessageAsync(SessionContext context, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

	/// <summary>
	/// Called when the session ends.
	/// </summary>
	Task OnSessionClosedAsync(SessionContext context, SessionCloseReason reason, string? description, CancellationToken cancellationToken);
}

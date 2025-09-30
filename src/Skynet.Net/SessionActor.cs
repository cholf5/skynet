using Microsoft.Extensions.Logging;
using Skynet.Core;

namespace Skynet.Net;

/// <summary>
/// Actor responsible for binding an external client connection to the actor runtime.
/// </summary>
public sealed class SessionActor : Actor
{
	private readonly ISessionConnection _connection;
	private readonly SessionMetadata _metadata;
	private readonly Func<SessionContext, ISessionMessageRouter> _routerFactory;
	private ISessionMessageRouter? _router;
	private SessionContext? _context;
	private bool _closing;
	private bool _notified;

	internal SessionActor(ISessionConnection connection, SessionMetadata metadata, Func<SessionContext, ISessionMessageRouter> routerFactory)
	{
		_connection = connection ?? throw new ArgumentNullException(nameof(connection));
		_metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
		_routerFactory = routerFactory ?? throw new ArgumentNullException(nameof(routerFactory));
	}

	/// <inheritdoc />
	protected override async ValueTask HandleStartAsync(CancellationToken cancellationToken)
	{
		_context = new SessionContext(System, Self, _connection, _metadata, Logger);
		_router = _routerFactory(_context) ?? throw new InvalidOperationException("The session router factory returned null.");
		await _router.OnSessionStartedAsync(_context, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	protected override async Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
	{
		switch (envelope.Payload)
		{
			case SessionInboundMessage inbound:
				_connection.MarkActivity();
				await EnsureRouter().OnSessionMessageAsync(EnsureContext(), inbound.Payload, cancellationToken).ConfigureAwait(false);
				break;
			case SessionOutboundMessage outbound:
				_connection.MarkActivity();
				await _connection.SendAsync(outbound.Payload, cancellationToken).ConfigureAwait(false);
				break;
			case SessionCloseMessage close:
				await CloseAsync(close.Reason, close.Description, cancellationToken).ConfigureAwait(false);
				break;
			case SessionHeartbeatTimeoutMessage timeout:
				await CloseAsync(SessionCloseReason.HeartbeatTimeout, $"Client idle for {timeout.Timeout}.", cancellationToken).ConfigureAwait(false);
				break;
			case SessionClientClosedMessage closed:
				await NotifyClosedAsync(closed.Reason, closed.Description, cancellationToken).ConfigureAwait(false);
				_closing = true;
				_ = System.KillAsync(Self);
				break;
			default:
				throw new InvalidOperationException($"Unsupported session payload type {envelope.Payload?.GetType().FullName}.");
		}

		return null;
	}

	/// <inheritdoc />
	protected override async ValueTask HandleStopAsync(CancellationToken cancellationToken)
	{
		await NotifyClosedAsync(SessionCloseReason.ServerShutdown, "Session actor shutting down.", cancellationToken).ConfigureAwait(false);
		await _connection.DisposeAsync().ConfigureAwait(false);
	}

	private ISessionMessageRouter EnsureRouter()
	{
		return _router ?? throw new InvalidOperationException("Session router has not been initialized.");
	}

	private SessionContext EnsureContext()
	{
		return _context ?? throw new InvalidOperationException("Session context has not been initialized.");
	}

	private async ValueTask CloseAsync(SessionCloseReason reason, string? description, CancellationToken cancellationToken)
	{
		if (_closing)
		{
			return;
		}

		_closing = true;
		try
		{
			await _connection.CloseAsync(reason, description, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			Logger.LogWarning(ex, "Failed to close session {SessionId} gracefully.", _metadata.SessionId);
		}
		await NotifyClosedAsync(reason, description, cancellationToken).ConfigureAwait(false);
		_ = System.KillAsync(Self);
	}

	private async ValueTask NotifyClosedAsync(SessionCloseReason reason, string? description, CancellationToken cancellationToken)
	{
		if (_notified)
		{
			return;
		}

		_notified = true;
		var context = EnsureContext();
		context.MarkClosed();
		if (_router is not null)
		{
			await _router.OnSessionClosedAsync(context, reason, description, cancellationToken).ConfigureAwait(false);
		}
	}
}

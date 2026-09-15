using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Skynet.Core;

namespace Skynet.Net;

/// <summary>
/// Handles one business command dispatched by <see cref="MsgServerRouter"/>. The <paramref name="payload"/>
/// argument contains the client frame with the leading command byte already stripped.
/// </summary>
/// <param name="context">The session the client frame arrived on.</param>
/// <param name="payload">The business payload of the frame (command byte removed).</param>
/// <param name="cancellationToken">Token that is cancelled when the session or the gate shuts down.</param>
public delegate ValueTask MsgCommandHandler(SessionContext context, ReadOnlyMemory<byte> payload,
	CancellationToken cancellationToken);

/// <summary>
/// Business routing layer ("MsgServer") that sits on top of <see cref="GateServer"/>. Client frames are
/// dispatched to registered handlers by the leading command byte, typically routing requests to game
/// server actors and writing their responses back to the calling session.
/// <para>
/// Wire convention (fixed): every client frame payload is <c>[command byte][business payload...]</c>.
/// The command byte is consumed by the router; handlers receive only the business payload. Responses
/// written by handlers are opaque to the router, but the first byte of a response must not be
/// <see cref="ErrorFrameMarker"/> (0x00) so clients can tell error frames apart from command responses.
/// </para>
/// <para>
/// Error frame convention (fixed): <c>[0x00][original command byte][UTF-8 error text]</c>, where the
/// error text is at most <see cref="MaxErrorTextBytes"/> bytes. Error frames are emitted for frames
/// without a command byte (<c>"empty frame"</c>), for unregistered command numbers
/// (<c>"unknown command 0xNN"</c>) and when a handler throws
/// (<c>"handler error: ExceptionType: message"</c>). Handler failures never close the session.
/// </para>
/// <para>
/// The command table is populated once during startup via <see cref="RegisterCommand"/> or
/// <see cref="ForwardCommand"/>; dispatch performs dictionary lookups only, with no reflection.
/// Registration is rejected once the router has started serving sessions, which makes a shared
/// instance safe to read from concurrent sessions (pass it to
/// <see cref="GateServerOptions.RouterFactory"/> for every session).
/// </para>
/// </summary>
public sealed class MsgServerRouter : ISessionMessageRouter
{
	/// <summary>
	/// The command byte reserved for error frames. Clients should treat any frame whose first byte is
	/// <c>0x00</c> as <c>[0x00][command][error text]</c>; the command number <c>0x00</c> itself cannot be
	/// registered.
	/// </summary>
	public const byte ErrorFrameMarker = 0x00;

	/// <summary>
	/// Maximum length of the UTF-8 error text carried by an error frame. Longer texts are truncated.
	/// </summary>
	public const int MaxErrorTextBytes = 256;

	private const string EmptyFrameErrorText = "empty frame";
	private const string UnknownCommandErrorFormat = "unknown command 0x{0:X2}";
	private const string HandlerErrorTextPrefix = "handler error";

	private readonly ConcurrentDictionary<byte, MsgCommandHandler> _handlers = new();
	private readonly ILogger _logger;
	private bool _started;

	/// <summary>
	/// Initializes a new, empty router. Register commands before handing the router to
	/// <see cref="GateServerOptions.RouterFactory"/>.
	/// </summary>
	/// <param name="logger">Optional logger used to report handler failures and undeliverable error frames.</param>
	public MsgServerRouter(ILogger? logger = null)
	{
		_logger = logger ?? NullLogger.Instance;
	}

	/// <summary>
	/// Registers a handler for a command number. Registering the same command number twice throws
	/// <see cref="ArgumentException"/>, as does the reserved command number <see cref="ErrorFrameMarker"/>.
	/// Registration is only allowed while the router has not started serving sessions.
	/// </summary>
	/// <param name="command">The command byte carried as the first byte of client frames.</param>
	/// <param name="handler">The handler invoked with the business payload (command byte stripped).</param>
	public void RegisterCommand(byte command, MsgCommandHandler handler)
	{
		ArgumentNullException.ThrowIfNull(handler);
		ThrowIfReserved(command);
		ThrowIfFrozen();
		if (!_handlers.TryAdd(command, handler))
		{
			throw new ArgumentException($"Command 0x{command:X2} is already registered.", nameof(command));
		}
	}

	/// <summary>
	/// Registers a built-in forwarding handler for a command number: the business payload is delivered to
	/// <paramref name="target"/> as a <c>byte[]</c> request via <see cref="SessionContext.CallAsync{TResponse}"/>,
	/// and the response is written back to the client. Supported response types are <c>byte[]</c>,
	/// <c>ReadOnlyMemory&lt;byte&gt;</c> and <c>string</c> (UTF-8 encoded); any other response type raises an
	/// error frame. If the target actor fails or times out, the client receives an error frame and the
	/// session stays alive.
	/// </summary>
	/// <param name="command">The command byte carried as the first byte of client frames.</param>
	/// <param name="target">The actor that serves the command, usually a game server actor.</param>
	/// <param name="callTimeout">Optional timeout applied to the actor call.</param>
	public void ForwardCommand(byte command, ActorHandle target, TimeSpan? callTimeout = null)
	{
		if (!target.IsValid)
		{
			throw new ArgumentException("Forward target must be a valid actor handle.", nameof(target));
		}

		RegisterCommand(command, (context, payload, cancellationToken) =>
			ForwardCoreAsync(context, target, payload, callTimeout, cancellationToken));
	}

	/// <summary>
	/// Gets a value indicating whether a handler is registered for the specified command number.
	/// </summary>
	/// <param name="command">The command byte to look up.</param>
	public bool IsRegistered(byte command)
	{
		return _handlers.ContainsKey(command);
	}

	/// <inheritdoc />
	public Task OnSessionStartedAsync(SessionContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);
		// Freeze the command table: from now on dispatches read it concurrently, so no further
		// registrations are accepted. Registration racing with the very first session start is a
		// startup bug either way; the flag makes it fail loudly instead of corrupting the table.
		Volatile.Write(ref _started, true);
		return Task.CompletedTask;
	}

	/// <summary>
	/// Dispatches one client frame by its leading command byte. Frames without a command byte and frames
	/// carrying an unregistered command number are answered with an error frame; handler exceptions are
	/// logged, answered with an error frame and leave the session alive.
	/// </summary>
	public async Task OnSessionMessageAsync(SessionContext context, ReadOnlyMemory<byte> payload,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);
		if (payload.Length == 0)
		{
			await SendErrorFrameAsync(context, ErrorFrameMarker, EmptyFrameErrorText, cancellationToken)
				.ConfigureAwait(false);
			return;
		}

		var command = payload.Span[0];
		if (!_handlers.TryGetValue(command, out var handler))
		{
			await SendErrorFrameAsync(context, command, string.Format(UnknownCommandErrorFormat, command),
				cancellationToken).ConfigureAwait(false);
			return;
		}

		try
		{
			await handler(context, payload[1..], cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// Session or server shutdown is not a business error; let it propagate untouched.
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "MsgServer handler for command 0x{Command:X2} failed on session {SessionId}.",
				command, context.Metadata.SessionId);
			await SendErrorFrameAsync(context, command, DescribeError(ex), cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// No-op: command dispatch is stateless, so per-session cleanup is the handler's responsibility
	/// (session-scoped state belongs in <see cref="SessionContext.Items"/>).
	/// </summary>
	public Task OnSessionClosedAsync(SessionContext context, SessionCloseReason reason, string? description,
		CancellationToken cancellationToken)
	{
		return Task.CompletedTask;
	}

	/// <summary>
	/// Encodes the wire format of an error frame: <c>[0x00][command][UTF-8 error text]</c> with the text
	/// truncated to <see cref="MaxErrorTextBytes"/> bytes.
	/// </summary>
	internal static byte[] EncodeErrorFrame(byte command, string errorText)
	{
		var textBytes = Encoding.UTF8.GetBytes(errorText);
		if (textBytes.Length > MaxErrorTextBytes)
		{
			textBytes = TruncateUtf8(errorText, MaxErrorTextBytes);
		}

		var frame = new byte[2 + textBytes.Length];
		frame[0] = ErrorFrameMarker;
		frame[1] = command;
		textBytes.CopyTo(frame, 2);
		return frame;
	}

	private static byte[] TruncateUtf8(string text, int maxBytes)
	{
		// Binary-search the longest character prefix whose UTF-8 encoding fits, so multi-byte
		// sequences are never split and arbitrarily long error texts stay cheap to cut.
		var low = 0;
		var high = text.Length;
		while (low < high)
		{
			var middle = (low + high + 1) / 2;
			if (Encoding.UTF8.GetByteCount(text.AsSpan(0, middle)) <= maxBytes)
			{
				low = middle;
			}
			else
			{
				high = middle - 1;
			}
		}

		var buffer = new byte[Encoding.UTF8.GetByteCount(text.AsSpan(0, low))];
		Encoding.UTF8.GetBytes(text.AsSpan(0, low), buffer);
		return buffer;
	}

	private static string DescribeError(Exception exception)
	{
		var type = exception.GetType().Name;
		return string.IsNullOrEmpty(exception.Message)
			? $"{HandlerErrorTextPrefix}: {type}"
			: $"{HandlerErrorTextPrefix}: {type}: {exception.Message}";
	}

	private static async ValueTask ForwardCoreAsync(SessionContext context, ActorHandle target,
		ReadOnlyMemory<byte> payload, TimeSpan? callTimeout, CancellationToken cancellationToken)
	{
		// Snapshot the payload: the router-owned buffer must not be handed to actors as-is.
		var request = payload.ToArray();
		var response = await context.CallAsync<object>(target, request, callTimeout, cancellationToken)
			.ConfigureAwait(false);
		await context.SendAsync(EncodeForwardResponse(response), cancellationToken).ConfigureAwait(false);
	}

	private static ReadOnlyMemory<byte> EncodeForwardResponse(object? response)
	{
		return response switch
		{
			null => throw new InvalidOperationException("The target actor returned a null response."),
			byte[] bytes => bytes,
			ReadOnlyMemory<byte> memory => memory,
			string text => Encoding.UTF8.GetBytes(text),
			_ => throw new InvalidOperationException(
				$"The target actor returned unsupported response type {response.GetType().FullName}; expected byte[], ReadOnlyMemory<byte> or string.")
		};
	}

	private async ValueTask SendErrorFrameAsync(SessionContext context, byte command, string errorText,
		CancellationToken cancellationToken)
	{
		try
		{
			await context.SendAsync(EncodeErrorFrame(command, errorText), cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			// The connection is usually gone when this fails; the session actor owns the teardown.
			_logger.LogDebug(ex, "Failed to deliver MsgServer error frame for command 0x{Command:X2} on session {SessionId}.",
				command, context.Metadata.SessionId);
		}
	}

	private void ThrowIfReserved(byte command)
	{
		if (command == ErrorFrameMarker)
		{
			throw new ArgumentException("Command 0x00 is reserved for error frames.", nameof(command));
		}
	}

	private void ThrowIfFrozen()
	{
		if (Volatile.Read(ref _started))
		{
			throw new InvalidOperationException("Commands cannot be registered after the router started serving sessions.");
		}
	}
}

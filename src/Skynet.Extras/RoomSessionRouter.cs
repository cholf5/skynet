using System.Collections.Generic;
using System.Linq;
using System.Text;
using Skynet.Net;

namespace Skynet.Extras;

/// <summary>
/// Session router that provides a text-based room management protocol on top of <see cref="RoomManager"/>.
/// </summary>
public sealed class RoomSessionRouter : ISessionMessageRouter
{
	private const string MembershipKey = "skynet.rooms.members";
	private const string AliasKey = "skynet.rooms.alias";
        private readonly RoomManager _manager;
        private readonly string _defaultRoom;
        private readonly Func<SessionContext, string>? _welcomeFormatter;

        public RoomSessionRouter(RoomManager manager, string defaultRoom = "lobby", Func<SessionContext, string>? welcomeFormatter = null)
        {
                _manager = manager ?? throw new ArgumentNullException(nameof(manager));
                if (string.IsNullOrWhiteSpace(defaultRoom))
                {
                        throw new ArgumentException("Default room must be provided.", nameof(defaultRoom));
                }

                _defaultRoom = NormalizeRoom(defaultRoom);
                _welcomeFormatter = welcomeFormatter;
        }

	public async Task OnSessionStartedAsync(SessionContext context, CancellationToken cancellationToken)
	{
		var alias = EnsureAlias(context);
		var rooms = EnsureMembership(context);
		rooms.Add(_defaultRoom);
		var joinResult = _manager.Join(_defaultRoom, new RoomParticipant(context.SessionHandle, context.Metadata));
		await context.SendAsync(_welcomeFormatter?.Invoke(context) ?? $"WELCOME {alias} room={_defaultRoom}", cancellationToken).ConfigureAwait(false);
		if (joinResult.Added)
		{
			await BroadcastSystemAsync(context, _defaultRoom, $"{alias} joined", cancellationToken).ConfigureAwait(false);
		}
	}

	public async Task OnSessionMessageAsync(SessionContext context, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
	{
		var text = Encoding.UTF8.GetString(payload.Span).Trim();
		if (string.IsNullOrEmpty(text))
		{
			return;
		}

		var parts = text.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
		var command = parts[0].ToLowerInvariant();
		switch (command)
		{
			case "join":
				await HandleJoinAsync(context, parts, cancellationToken).ConfigureAwait(false);
				break;
			case "leave":
				await HandleLeaveAsync(context, parts, cancellationToken).ConfigureAwait(false);
				break;
			case "say":
			case "send":
				await HandleBroadcastAsync(context, parts, cancellationToken).ConfigureAwait(false);
				break;
			case "rooms":
				await HandleListRoomsAsync(context, cancellationToken).ConfigureAwait(false);
				break;
			case "who":
				await HandleWhoAsync(context, parts, cancellationToken).ConfigureAwait(false);
				break;
			case "nick":
				await HandleNickAsync(context, parts, cancellationToken).ConfigureAwait(false);
				break;
			default:
				await context.SendAsync($"ERR unknown-command {command}", cancellationToken).ConfigureAwait(false);
				break;
		}
	}

	public async Task OnSessionClosedAsync(SessionContext context, SessionCloseReason reason, string? description, CancellationToken cancellationToken)
	{
		var alias = EnsureAlias(context);
		var rooms = EnsureMembership(context);
		foreach (var room in rooms.ToArray())
		{
			_manager.Leave(room, context.SessionHandle);
			await BroadcastSystemAsync(context, room, $"{alias} left", cancellationToken).ConfigureAwait(false);
		}
		_manager.RemoveSession(context.SessionHandle);
	}

	private async Task HandleJoinAsync(SessionContext context, string[] parts, CancellationToken cancellationToken)
	{
		if (parts.Length < 2)
		{
			await context.SendAsync("ERR missing-room", cancellationToken).ConfigureAwait(false);
			return;
		}

		var room = NormalizeRoom(parts[1]);
		var rooms = EnsureMembership(context);
		if (!rooms.Add(room))
		{
			await context.SendAsync($"INFO already-in {room}", cancellationToken).ConfigureAwait(false);
			return;
		}

		var alias = EnsureAlias(context);
		var result = _manager.Join(room, new RoomParticipant(context.SessionHandle, context.Metadata));
		await context.SendAsync($"JOINED {room} members={_manager.GetMembers(room).Count}", cancellationToken).ConfigureAwait(false);
		if (result.Added)
		{
			await BroadcastSystemAsync(context, room, $"{alias} joined", cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task HandleLeaveAsync(SessionContext context, string[] parts, CancellationToken cancellationToken)
	{
		if (parts.Length < 2)
		{
			await context.SendAsync("ERR missing-room", cancellationToken).ConfigureAwait(false);
			return;
		}

		var room = NormalizeRoom(parts[1]);
		var rooms = EnsureMembership(context);
		if (!rooms.Remove(room))
		{
			await context.SendAsync($"INFO not-in {room}", cancellationToken).ConfigureAwait(false);
			return;
		}

		_manager.Leave(room, context.SessionHandle);
		await context.SendAsync($"LEFT {room}", cancellationToken).ConfigureAwait(false);
		await BroadcastSystemAsync(context, room, $"{EnsureAlias(context)} left", cancellationToken).ConfigureAwait(false);
	}

        private async Task HandleBroadcastAsync(SessionContext context, string[] parts, CancellationToken cancellationToken)
        {
                if (parts.Length < 3)
                {
                        await context.SendAsync("ERR missing-message", cancellationToken).ConfigureAwait(false);
                        return;
                }

                var room = NormalizeRoom(parts[1]);
                var message = parts[2];
                var rooms = EnsureMembership(context);
                if (!rooms.Contains(room))
                {
                        await context.SendAsync($"ERR not-in {room}", cancellationToken).ConfigureAwait(false);
                        return;
                }

                var alias = EnsureAlias(context);
                var formatted = $"[{room}] {alias}: {message}";
                await _manager.BroadcastTextAsync(room, formatted, cancellationToken).ConfigureAwait(false);
        }

        private Task HandleListRoomsAsync(SessionContext context, CancellationToken cancellationToken)
        {
                var rooms = EnsureMembership(context);
                var payload = rooms.Count == 0 ? "ROOMS none" : $"ROOMS {string.Join(',', rooms)}";
                return context.SendAsync(payload, cancellationToken);
        }

        private Task HandleWhoAsync(SessionContext context, string[] parts, CancellationToken cancellationToken)
        {
                if (parts.Length < 2)
                {
                        return context.SendAsync("ERR missing-room", cancellationToken);
                }

                var room = NormalizeRoom(parts[1]);
                var members = _manager.GetMembers(room);
                var payload = members.Count == 0 ? $"WHO {room} none" : $"WHO {room} {string.Join(',', members.Select(m => m.SessionId))}";
                return context.SendAsync(payload, cancellationToken);
        }

        private Task HandleNickAsync(SessionContext context, string[] parts, CancellationToken cancellationToken)
        {
                if (parts.Length < 2)
                {
                        return context.SendAsync("ERR missing-alias", cancellationToken);
                }

                var alias = parts[1].Trim();
                if (alias.Length == 0 || alias.Length > 32)
                {
                        return context.SendAsync("ERR invalid-alias", cancellationToken);
                }

                context.Items[AliasKey] = alias;
                return context.SendAsync($"NICK {alias}", cancellationToken);
        }

        private Task BroadcastSystemAsync(SessionContext context, string room, string message, CancellationToken cancellationToken)
        {
                return _manager.BroadcastTextAsync(room, $"[{room}] * {message}", cancellationToken);
        }

        private static HashSet<string> EnsureMembership(SessionContext context)
        {
                if (context.Items.TryGetValue(MembershipKey, out var value) && value is HashSet<string> rooms)
                {
                        return rooms;
                }

                rooms = new HashSet<string>(StringComparer.Ordinal);
                context.Items[MembershipKey] = rooms;
                return rooms;
        }

        private static string EnsureAlias(SessionContext context)
        {
                if (context.Items.TryGetValue(AliasKey, out var value) && value is string alias)
                {
                        return alias;
                }

                alias = context.Metadata.SessionId;
                context.Items[AliasKey] = alias;
                return alias;
        }

        private static string NormalizeRoom(string room)
        {
                return room.Trim().ToLowerInvariant();
        }
}

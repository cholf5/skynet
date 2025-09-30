using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Skynet.Core;
using Skynet.Net;

namespace Skynet.Extras;

/// <summary>
/// Provides membership and broadcast management for logical rooms that map to session actors.
/// </summary>
public sealed class RoomManager
{
	private readonly ActorSystem _system;
	private readonly ILogger<RoomManager> _logger;
	private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<ActorHandle, ConcurrentDictionary<string, byte>> _memberships = new();

	public RoomManager(ActorSystem system, ILogger<RoomManager>? logger = null)
	{
		_system = system ?? throw new ArgumentNullException(nameof(system));
		_logger = logger ?? NullLogger<RoomManager>.Instance;
	}

	/// <summary>
	/// Registers a participant with the specified room.
	/// </summary>
	public RoomJoinResult Join(string roomId, RoomParticipant participant)
	{
		if (string.IsNullOrWhiteSpace(roomId))
		{
			throw new ArgumentException("Room id must be provided.", nameof(roomId));
		}

		if (!participant.Handle.IsValid)
		{
			throw new ArgumentException("Participant handle must be valid.", nameof(participant));
		}

		var room = _rooms.GetOrAdd(roomId, static id => new Room(id));
		var member = room.AddOrUpdate(participant, out var added);
		var membership = _memberships.GetOrAdd(participant.Handle, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
		membership[roomId] = 0;
		var isNewRoom = added && room.Count == 1;
		return new RoomJoinResult(member, added, isNewRoom);
	}

	/// <summary>
	/// Removes a participant from the specified room.
	/// </summary>
	public RoomLeaveResult Leave(string roomId, ActorHandle participant)
	{
		return LeaveInternal(roomId, participant, updateMembership: true);
	}

	/// <summary>
	/// Removes a participant from all rooms they are currently in.
	/// </summary>
	public int RemoveSession(ActorHandle participant)
	{
		if (!_memberships.TryRemove(participant, out var rooms))
		{
			return 0;
		}

		var removed = 0;
		foreach (var room in rooms.Keys)
		{
			var result = LeaveInternal(room, participant, updateMembership: false);
			if (result.Member is not null)
			{
				removed++;
			}
		}

		return removed;
	}

	/// <summary>
	/// Gets a snapshot of the members in the specified room.
	/// </summary>
	public IReadOnlyList<RoomMember> GetMembers(string roomId)
	{
		if (!_rooms.TryGetValue(roomId, out var room))
		{
			return Array.Empty<RoomMember>();
		}

		return room.Snapshot();
	}

	/// <summary>
	/// Broadcasts a binary payload to all members of the specified room.
	/// </summary>
	public async Task<RoomBroadcastResult> BroadcastAsync(string roomId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
	{
		if (!_rooms.TryGetValue(roomId, out var room))
		{
			return RoomBroadcastResult.Empty;
		}

		var members = room.Snapshot();
		if (members.Count == 0)
		{
			return RoomBroadcastResult.Empty;
		}

		var attempts = members.Count;
		var delivered = 0;
		var failures = new List<RoomMember>();
		foreach (var member in members)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				await _system.SendAsync(member.SessionHandle, new SessionOutboundMessage(payload), cancellationToken: cancellationToken).ConfigureAwait(false);
				delivered++;
			}
			catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
			{
				_logger.LogDebug(ex, "Removing stale session {SessionId} from room {RoomId}.", member.SessionId, roomId);
				failures.Add(member);
			}
		}

		foreach (var failure in failures)
		{
			LeaveInternal(roomId, failure.SessionHandle, updateMembership: true);
		}

		return new RoomBroadcastResult(attempts, delivered, failures.Count);
	}

	/// <summary>
	/// Broadcasts a UTF-8 text payload to all members of the specified room.
	/// </summary>
	public Task<RoomBroadcastResult> BroadcastTextAsync(string roomId, string text, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(text);
		return BroadcastAsync(roomId, Encoding.UTF8.GetBytes(text), cancellationToken);
	}

	private RoomLeaveResult LeaveInternal(string roomId, ActorHandle participant, bool updateMembership)
	{
		if (string.IsNullOrWhiteSpace(roomId))
		{
			throw new ArgumentException("Room id must be provided.", nameof(roomId));
		}

		if (!_rooms.TryGetValue(roomId, out var room))
		{
			return RoomLeaveResult.Empty;
		}

		if (!room.TryRemove(participant, out var member))
		{
			return RoomLeaveResult.Empty;
		}

		if (updateMembership && _memberships.TryGetValue(participant, out var membership))
		{
			membership.TryRemove(roomId, out _);
			if (membership.IsEmpty)
			{
				_memberships.TryRemove(participant, out _);
			}
		}

		var empty = room.IsEmpty;
		if (empty)
		{
			_rooms.TryRemove(roomId, out _);
		}

		return new RoomLeaveResult(member, empty);
	}

	private sealed class Room
	{
		private readonly ConcurrentDictionary<ActorHandle, RoomMember> _members = new();

		internal Room(string id)
		{
			Id = id;
		}

		public string Id { get; }

		public int Count => _members.Count;

		public bool IsEmpty => _members.IsEmpty;

		internal RoomMember AddOrUpdate(RoomParticipant participant, out bool added)
		{
			while (true)
			{
				if (_members.TryGetValue(participant.Handle, out var existing))
				{
					added = false;
					if (!ReferenceEquals(existing.Metadata, participant.Metadata))
					{
						var updated = existing with { Metadata = participant.Metadata };
						if (_members.TryUpdate(participant.Handle, updated, existing))
						{
							return updated;
						}

						continue;
					}

					return existing;
				}

				var member = new RoomMember(Id, participant.Handle, participant.Metadata.SessionId, participant.Metadata, DateTimeOffset.UtcNow);
				if (_members.TryAdd(participant.Handle, member))
				{
					added = true;
					return member;
				}
			}
		}

		internal bool TryRemove(ActorHandle participant, out RoomMember member)
		{
			return _members.TryRemove(participant, out member);
		}

		internal IReadOnlyList<RoomMember> Snapshot()
		{
			return _members.Values.ToArray();
		}
	}
}

/// <summary>
/// Describes a participant that can join a room.
/// </summary>
public readonly record struct RoomParticipant(ActorHandle Handle, SessionMetadata Metadata);

/// <summary>
/// Represents the outcome of a join operation.
/// </summary>
public readonly record struct RoomJoinResult(RoomMember Member, bool Added, bool CreatedRoom);

/// <summary>
/// Represents the outcome of a leave operation.
/// </summary>
public readonly record struct RoomLeaveResult(RoomMember? Member, bool RoomEmpty)
{
	public static RoomLeaveResult Empty { get; } = new(null, false);
}

/// <summary>
/// Summarizes a broadcast operation.
/// </summary>
public readonly record struct RoomBroadcastResult(int Attempted, int Delivered, int Evicted)
{
	public static RoomBroadcastResult Empty { get; } = new(0, 0, 0);
}

/// <summary>
/// Describes a room member snapshot.
/// </summary>
public sealed record RoomMember(string RoomId, ActorHandle SessionHandle, string SessionId, SessionMetadata Metadata, DateTimeOffset JoinedAt);

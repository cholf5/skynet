# RoomManager & Multicast Notes

## Overview

`RoomManager` keeps track of room membership by mapping `ActorHandle` identifiers to rooms. Each broadcast fans out a `SessionOutboundMessage` to the registered session actors, automatically evicting stale handles when the underlying actor has been terminated.

`RoomSessionRouter` builds on top of the manager and provides a text-based protocol for clients connected through the `GateServer`. It supports the following commands:

| Command | Description |
| --- | --- |
| `join <room>` | Join (or create) a room |
| `leave <room>` | Leave the specified room |
| `say <room> <message>` | Broadcast a UTF-8 message to all members |
| `rooms` | List rooms joined by the current session |
| `who <room>` | List session identifiers currently inside the room |
| `nick <alias>` | Update the alias used when broadcasting |

## Benchmarking

Run the built-in benchmark to stress the multicast pipeline without external clients:

```bash
dotnet run --project src/Skynet.Examples/Skynet.Examples.csproj -- --rooms-bench
```

The benchmark spins up in-process loopback actors, joins them to a shared room, and issues 1,000 broadcast rounds to 200 simulated sessions. The console output reports end-to-end latency and effective throughput.

## Testing

Automated coverage lives in:

- `tests/Skynet.Extras.Tests/RoomManagerTests.cs` – unit tests for membership and broadcast semantics.
- `tests/Skynet.Net.Tests/RoomSessionRouterTests.cs` – integration tests covering command parsing and multi-session broadcasts.

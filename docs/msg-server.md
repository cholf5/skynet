# MsgServer (Business Routing Layer)

## Overview

`MsgServerRouter` (namespace `Skynet.Net`) implements the PRD 11.3 business routing layer that sits on
top of `GateServer`. It implements `ISessionMessageRouter`, so it plugs directly into
`GateServerOptions.RouterFactory` and turns raw client frames into typed, command-number based
dispatches to registered handlers — typically game server actors — and writes their responses back to
the calling session.

Dispatch is a plain dictionary lookup on the command byte: registration happens once during startup,
there is no reflection, and a fully configured instance is safe to share across all sessions of a gate.

## Wire protocol

Every client frame payload has the fixed layout:

```
[command byte][business payload ...]
```

- The first byte is the command number. It is consumed by the router; handlers receive only the
  business payload.
- Handler responses are opaque to the router, but the first byte of a response **must not** be `0x00`
  (see error frames below), otherwise clients cannot distinguish responses from errors.

### Error frames

Error frames use the fixed layout (constant `MsgServerRouter.ErrorFrameMarker = 0x00`):

```
[0x00][original command byte][UTF-8 error text]
```

The error text is capped at `MsgServerRouter.MaxErrorTextBytes` (256) bytes and is truncated on a
character boundary (never mid UTF-8 sequence). Error frames are produced in exactly three cases:

| Situation | Error text |
| --- | --- |
| Frame without a command byte (empty payload) | `empty frame` (command byte `0x00`) |
| Command number without a registered handler | `unknown command 0xNN` |
| A handler (or forwarded actor call) threw | `handler error: ExceptionType: message` |

Handler failures are logged (`Warning`) and answered with an error frame; **the session stays alive**
and all subsequent frames keep being dispatched. `OperationCanceledException` raised by session/server
shutdown is not converted into an error frame.

Command number `0x00` is reserved and cannot be registered, so a frame starting with `0x00` always
means "error frame follows" on the wire.

## Registration API

Registration is startup-time only. Once the first session has started (`OnSessionStartedAsync`), the
router is frozen and any further registration throws `InvalidOperationException`. Duplicate command
numbers and the reserved command `0x00` throw `ArgumentException`.

### Custom handlers

```csharp
var msgServer = new MsgServerRouter();

// 0x01 "login": decode the payload, call an actor, write the response back to the client.
msgServer.RegisterCommand(0x01, async (context, payload, cancellationToken) =>
{
	var request = Encoding.UTF8.GetString(payload.Span);
	var token = await context.CallAsync<string>(loginService, new LoginRequest(request),
		cancellationToken: cancellationToken);
	await context.SendAsync(Encoding.UTF8.GetBytes(token), cancellationToken);
});
```

The `MsgCommandHandler` delegate receives the `SessionContext` (for `SendAsync`, `CallAsync`,
`ForwardAsync`, per-session `Items`), the business payload without the command byte, and the session
`CancellationToken`.

### Built-in forwarding

`ForwardCommand` wires a command number straight to a target actor: the business payload is delivered
to the actor as a `byte[]` request via `SessionContext.CallAsync`, and the response is written back to
the client.

```csharp
msgServer.ForwardCommand(0x02, battleServer.Handle, callTimeout: TimeSpan.FromSeconds(5));
```

Supported actor response types are `byte[]`, `ReadOnlyMemory<byte>` and `string` (UTF-8 encoded). Any
other response type (or a `null` response) produces the standard error frame. For typed
request/response contracts, register a custom handler that decodes the payload and calls the actor.

### Wiring into the gate

Create one router, register commands, then hand it to the gate factory for every session:

```csharp
var options = new GateServerOptions
{
	TcpPort = 2112,
	RouterFactory = _ => msgServer
};
await using var gate = new GateServer(system, options);
```

`IsRegistered(byte command)` reports whether a command number has a handler.

## Relationship with RoomSessionRouter

Both are `ISessionMessageRouter` implementations, and a gate connects exactly one router factory:

- `RoomSessionRouter` (in `Skynet.Extras`) implements a human-readable, text-based room chat protocol.
- `MsgServerRouter` (in `Skynet.Net`) implements the binary command-number dispatch used by game
  clients (PRD 11.3).

They target different protocols; pick one per gate. Custom `ISessionMessageRouter` implementations can
compose with either pattern (e.g. run `MsgServerRouter` for business frames and add lifecycle logic in
`OnSessionStartedAsync`/`OnSessionClosedAsync`).

## Testing

Automated coverage lives in `tests/Skynet.Net.Tests/MsgServerRouterTests.cs`:

- Unit tests exercise registration rules, unknown/empty frames, handler and target-actor failures,
  forwarding response encoding and error-frame truncation against an in-memory session connection.
- Gate integration tests drive the full client → TCP gate → MsgServer → actor → client round trip with
  real sockets, including error frames leaving the connection alive.

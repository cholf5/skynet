# Gate Encryption Handshake (RSA token handshake)

The Gate supports an optional encryption handshake: a client connects, exchanges a token for a session
key with the gate, and all business frames are then AES-GCM encrypted. This document describes the
Skynet implementation (task C-6). The handshake state machine lives in `src/Skynet.Net/Encryption/` and is
transport agnostic: it is driven by the gate receive loop for both TCP and WebSocket connections.

## Configuration

All settings live on `GateServerOptions`:

| Option | Default | Meaning |
| --- | --- | --- |
| `EnableEncryption` | `false` | Require the handshake before any business frame is accepted. |
| `RsaKeyProvider` | `null` | RSA keypair source. When null and encryption is enabled, the gate generates a fresh random 2048-bit keypair on every `StartAsync()`. Provide a persistent implementation in production. |
| `HandshakeTimeout` | `10 s` | Maximum time a connection may stay in the handshake phase before the gate closes it. |
| `TokenLifetime` | `30 s` | Lifetime of a handshake token issued by the gate. |
| `SessionCipherId` | `0x01` (AES-256-GCM) | Session cipher announced to clients. |
| `HkdfSalt` | built-in default | Explicit HKDF-SHA256 salt; both endpoints must agree. |
| `AuthCallback` | `null` | Authentication hook, see below. |

> **Production guidance:** `EnableEncryption` defaults to `false` only for wire compatibility with
> unencrypted clients. Any gate exposed to an untrusted network should set `EnableEncryption = true`
> and supply a persistent `IGateRsaKeyProvider`.

## Handshake protocol

Gate framing (TCP) is a 4-byte big-endian length prefix followed by the payload; WebSocket connections
carry the same payloads as binary messages. During the handshake phase the first payload byte is the
frame type:

| Value | Direction | Body |
| --- | --- | --- |
| `0x01` RequestToken | client → gate | empty |
| `0x02` ResponseToken | gate → client | token (32 B) ‖ expire unix ms (8 B, big-endian) |
| `0x03` ConfirmKey | client → gate | RSA-OAEP-SHA256 ciphertext (see plaintext layout below) |
| `0x04` ConfirmAck | gate → client | encrypted frame (see below); plaintext is the cipher id (1 B) |
| `0x05` Error | gate → client | UTF-8 error code (see `GateHandshakeErrors`) |

Handshake sequence:

1. Client sends `0x01`. The gate mints a random 32-byte token and returns it with an expiry timestamp.
2. Client generates a random 16-byte seed, derives the session key with HKDF-SHA256 and RSA-encrypts
   (seed ‖ echoed token, plus random padding) with the gate public key. It sends the ciphertext as `0x03`.
3. The gate RSA-decrypts the payload, compares the echoed token against the issued token with
   `CryptographicOperations.FixedTimeEquals`, derives the same session key, runs the authentication
   callback and only then creates the session actor. It sends `0x04`, encrypted with the session key —
   successful decryption proves to the client that the gate derived the same key.
4. All subsequent frames in both directions are encrypted.

Any violation (confirm before token, expired token, token mismatch, second handshake, business frame
before handshake, handshake timeout) produces a `0x05` error frame followed by a connection close.

### RSA plaintext layout (ConfirmKey)

Hand-written layout, replacing the protobuf envelope used by the reference implementation:

```
offset  size  field
0       4     magic "SGK1" (0x53 0x47 0x4B 0x31)
4       16    client seed
20      32    echoed server token
52      1     head padding length (5..20), followed by that many random bytes
...     1     tail padding length (13..30), followed by that many random bytes
```

Total plaintext length is 54 + head + tail (72..104 bytes). RSA padding is OAEP-SHA256
(`RSAEncryptionPadding.OaepSHA256`).

### Session key derivation

`HKDF-SHA256(extract salt, IKM = seed, info = "skynet-gate-session-key/v1" ‖ cipherId)` producing a
32-byte key. The salt can be overridden via `GateServerOptions.HkdfSalt`; the info string binds the key
to the protocol version and the negotiated cipher.

### Encrypted frame layout

```
nonce (12 B, random) || ciphertext || tag (16 B)
```

AES-256-GCM is the built-in cipher (`ISessionFrameCipher`, id `0x01`); the handshake layer is pluggable,
so other AEAD ciphers can be added by extending `SessionFrameCipherFactory` without touching the state
machine. After the handshake completes, inbound frames are always decrypted as a whole, so ciphertext
bytes can never be mistaken for a handshake frame type.

## Authentication hook

`GateServerOptions.AuthCallback` has the signature
`Func<GateAuthenticationContext, CancellationToken, ValueTask<bool>>` and receives the session metadata
plus the derived session key (null in plaintext mode). It runs after the handshake key confirmation
succeeded but before the session actor is created; returning false closes the connection with the
`GateHandshakeAuthRejected` error code. In plaintext mode the callback runs as soon as the connection is
accepted. No business frame is ever delivered to a session actor before authentication has passed.

## Error codes

Stable string codes are defined in `GateHandshakeErrors` (namespace `Skynet.Net.Encryption`), e.g.
`GateHandshakeInvalidToken` (replay), `GateHandshakeTokenExpired`, `GateHandshakeUnexpectedConfirm`,
`GateHandshakeDuplicateHandshake`, `GateHandshakeBusinessFrameRejected`, `GateHandshakeTimeout`,
`GateHandshakeAuthRejected`.

## Client usage

`GateEncryptionClientSession` (namespace `Skynet.Net.Encryption`) implements the client side:

```csharp
var session = new GateEncryptionClientSession(keyProvider);
await transport.SendAsync(GateEncryptionClientSession.CreateRequestTokenFrame());
var tokenFrame = await transport.ReceiveAsync();
var confirm = session.ProcessResponseToken(tokenFrame);
await transport.SendAsync(confirm);
var ack = await transport.ReceiveAsync();
session.ProcessConfirmAck(ack);
await session.Completion;               // handshake complete
transport.Send(GateFrameCodec.EncryptFrame(session.FrameCipher!, payload)); // business frames
```

A working reference client is `GateTestClient` in `tests/Skynet.Net.Tests/Encryption/`.

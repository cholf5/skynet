# Transports: TCP, KCP/UDP, and the ReliableQueue layer

Skynet nodes exchange actor envelopes over pluggable transports implementing `Skynet.Core.ITransport`.
This document describes the available transports, how to choose between them, and the design of the
protocol-independent reliable layer introduced with C-9.

## Overview

| Transport | Underlying channel | Reliability | Typical use |
|---|---|---|---|
| `TcpTransport` (`Skynet.Cluster`) | TCP | Stream, kernel-managed | Default for cross-node RPC |
| `KcpTransport` (`Skynet.Transport.Kcp`) | UDP + KCP (`ThirdParty/kcp2k`) | Reliable, ordered, message-oriented | Real-time workloads (battle sync) where TCP head-of-line blocking hurts |
| `ReliableQueue` (`Skynet.Transport.Kcp.Reliable`) | Any unreliable packet channel (usually UDP datagrams) | Sliding-window ARQ layer you stack yourself | Custom transports that get no reliability from their channel |

`TcpTransport` ships with `Skynet.Cluster`; `KcpTransport` is a transport plugin package
(`Skynet.Transport.Kcp`) that depends only on `Skynet.Core` — pull it in explicitly when a node
needs KCP/UDP connectivity. Both `TcpTransport` and `KcpTransport` implement the same
`ITransport` semantics: local delivery
short-circuit, cluster-registry routing, pending-call tracking for `CallAsync`, fault envelopes, and
heartbeat-based dead-peer detection. They are interchangeable from an `ActorSystem` perspective.

## TcpTransport

`TcpTransport` runs one TCP connection per remote node with a small custom framing protocol on top
(`[type:1][length:4 big-endian][payload]`, frame types `Handshake = 1`, `Envelope = 2`,
`Heartbeat = 3`). Connections are dialed lazily on the first send, arbitrated per node (outbound and
inbound races resolved through one gate), re-established automatically after a disconnect, and kept
alive with heartbeats plus `DeadNodeGracePeriod` dead-peer detection.

### TLS (optional)

Cross-node TCP links can be encrypted with TLS (`SslStream`). TLS is **off by default**: with no TLS
option set the transport behaves exactly like the plaintext implementation — no extra handshake, no
framing change, no measurable overhead.

Configuration lives on `TcpTransportOptions` and is per connection direction:

| Option | Side | Effect |
|---|---|---|
| `TlsServerCertificate` (`X509Certificate2?`) | server (inbound) | When set, every accepted connection is upgraded to TLS and must complete the server-side handshake with this certificate before the cluster handshake. `null` (default) keeps inbound connections plaintext. |
| `UseTls` (`bool`) | client (outbound) | When `true`, every dialed connection completes a client-side TLS handshake before sending the cluster handshake. Default `false` (plaintext). |
| `RemoteCertificateValidationCallback` | client (outbound) | Validates the server certificate during the outbound handshake. Return `true` to accept, `false` to reject (which fails the connection). `null` uses the platform default chain + name validation. Only used for outbound TLS; client certificates are not requested. |

A node that both accepts and dials other nodes typically sets both `TlsServerCertificate` and
`UseTls` (e.g. in a full mesh, where every node sets both). TLS is negotiated per connection
direction, so a hub that only receives dials needs only the certificate, and a leaf that only dials
needs only `UseTls`.

Wire order per connection: TCP connect → TLS handshake (only when configured for that direction) →
cluster handshake frames (`0x01`) → envelope/heartbeat frames. Because the TLS handshake always
precedes the first cluster frame, no plaintext frame bytes ever leak onto an encrypted link, and
everything above the stream is unchanged: frame parsing, heartbeats, pending-call tracking, and
reconnection behave identically over TLS. A reconnect dials a fresh TCP connection and therefore
always runs a fresh TLS handshake with a fresh `SslStream` — a re-used plaintext stream can never be
mistaken for an authenticated one.

#### Fail-fast on configuration mismatch

If one side of a link enables TLS and the other does not, the handshake can never complete. Both
sides fail their connection in bounded time instead of hanging:

- **TLS client → plaintext server**: the server parses the TLS `ClientHello` as a frame header whose
  announced length (~50 MB) exceeds `MaxFrameBytes` (default 16 MB) and immediately closes the
  connection; the client's handshake then fails on the closed stream. With the frame guard disabled
  (`MaxFrameBytes = int.MaxValue`) the client still gives up when `ConnectTimeout` elapses.
- **Plaintext client → TLS server**: the server's TLS handshake rejects the cluster frame bytes as
  an invalid TLS record and closes the connection; the client fails while waiting for the handshake
  reply (or on `ConnectTimeout`).

In both directions the TLS handshake shares the dial's `ConnectTimeout` budget, so the worst case is
a bounded, observable failure — never a stalled connection or an endlessly retried handshake. The
caller's `CallAsync` fails with the raw handshake error (an `AuthenticationException` or
`IOException`), the pending call is drained, and the server side logs
`Failed to process incoming connection` (warning level).

#### Limitations

- No mutual TLS: the server does not request client certificates, so
  `RemoteCertificateValidationCallback` only ever validates the server certificate.
- The client handshake's `TargetHost` is the registry endpoint address (an IP literal for static
  registries), so name-based certificate validation requires either DNS-based registry entries or a
  validation callback.
- No knobs for protocol versions or cipher suites (platform defaults apply — TLS 1.2/1.3); the
  server certificate must carry a private key usable for server authentication.

## KcpTransport

`KcpTransport` runs one KCP session per remote node over a single UDP socket bound to the node's
registry endpoint. It is built on a vendored copy of the kcp2k managed KCP state machine (MIT,
see `src/Skynet.Transport.Kcp/ThirdParty/kcp2k/`), isolated behind `KcpSession` so no
kcp2k type leaks into Skynet APIs.

### Wire layout

```
UDP datagram:  [ conversation id : 4 bytes, little-endian ][ KCP segment ... ]
KCP stream message (= one frame): [ frame type : 1 byte ][ payload ... ]
Frame types: Handshake = 1, Envelope = 2, Heartbeat = 3
```

KCP preserves message boundaries and fragments large messages internally (up to 255 fragments,
~288 KB at the default 1200 MTU), so frames need no length prefix. `KcpTransportOptions.MaxMessageBytes`
(default 256 KB) rejects larger frames up front.

The conversation id is allocated randomly by the client (never 0) and routes datagrams on the shared
socket to the right session. The server creates a session on the first datagram of an unknown
conversation.

### Handshake

No compatibility with any prior handshake is attempted. The handshake is a single reliable-ordered
frame exchange riding the KCP stream itself: the client sends its node id as the first frame; the
server registers the session (rejecting duplicates per node, arbitrating with outbound sessions
through the same per-node gate as TCP) and answers with its own node id. Because KCP delivery is
ordered, envelope frames can never precede the handshake.

### Threading model

Every connection owns a `KcpConnectionPump`: a `Channel<PumpOperation>` (Send / Input / Tick) with
`SingleReader = true` consumed by exactly one processing task. That task is the only caller of the
KCP state machine, which is not thread-safe and whose ordered semantics depend on serialized
access. Senders (transport `SendAsync`, UDP receive loop, timer) only enqueue operations and never
block on the state machine. A `PeriodicTimer` enqueues a Tick every `IntervalMilliseconds`
(default 10) to drive retransmission, acks, and window probing. This is the same "single-thread
pump" model the reliable layer prescribes (below).

### Dead-peer detection

Each session sends heartbeat frames every `HeartbeatInterval` (default 15 s). If no frame arrives
for `DeadNodeGracePeriod` (default 3x the heartbeat interval), the session is closed and every
pending call routed to that node fails with `RemoteConnectionClosedException`, mirroring
`TcpTransport`.

## ReliableQueue

`Skynet.Transport.Kcp.Reliable.ReliableQueue` is a protocol-independent sliding-window ARQ
layer (~250 lines, zero dependencies beyond the BCL) for transports whose underlying channel is a
raw unreliable packet pipe (typically UDP datagrams). It provides:

- Cumulative (`una`) plus selective (`sn`) acknowledgement over 16-bit sequence numbers with
  wraparound-safe ordering (`(short)(left - right) < 0`).
- A fixed send window (`windowSize`, default 32) with a buffer for messages that exceed it.
- Out-of-order receive buffering and in-order delivery, dropping packets more than one window
  ahead of the receive cursor.
- Fast retransmission after `fastResend` (default 3) duplicate acknowledgements.
- Dead-link detection after `deadLink` (default 20) transmissions of one segment.

### Ports from the reference implementation, plus fixes

The queue is a port of a proven internal implementation with four deliberate changes. The first two
were mandated by the task; the third was found by the loss-pipe tests during this task; the fourth
is a necessary consequence.

1. **Dead-link terminal state.** The original kept an exhausted segment in the send queue and
   re-invoked the broken callback on every subsequent `Update` tick. Now the first exhausted
   segment transitions the queue to a `Broken` terminal state: pending segments are dropped,
   `IsBroken` reports true, the callback fires exactly once, `Send` throws, and `Input`/`Update`
   are ignored until `Reset()` restores pristine state (sequence numbers and RTT estimator included).

2. **Adaptive RTO (RFC 6298 style).** The original used a fixed initial RTO with exponential
   backoff. Acknowledgements for segments transmitted exactly once (Karn's algorithm) now feed an
   SRTT/RTTVAR estimator; `CurrentRto` is clamped to `[minRto, MaxRto]` (defaults 100 ms / 60 s).
   Timeout retransmissions still double the segment RTO; fast-resend retransmissions do not.

3. **No acknowledgement without buffering.** The original acknowledged every data packet before
   any window check. Under loss plus reordering the receive window stalls behind a gap, and
   packets beyond the window were acknowledged while being dropped — the sender retired them and
   the messages were silently lost (observed as a permanent stall at ~47 of 500 messages in the
   loss-pipe tests). Packets beyond the receive window are now dropped *without* acknowledgement,
   so the sender keeps them unacked and its retransmission timer recovers the gap.

4. **Acknowledgements for already-delivered data.** Symmetrically, duplicates of delivered data are
   re-acknowledged explicitly (previously they were acked as a side effect of rule 3's old
   placement) so the peer's retransmission timer stops without redelivery.

### Threading model

The queue is deliberately lock-free and not thread-safe. `Send`, `Input`, and `Update` must be
called serially from a single driving context — a dedicated pump loop or actor. This is the same
model `KcpConnectionPump` implements for KCP; a transport that stacked ReliableQueue over raw UDP
would do the same. ReliableQueue is *not* stacked on KCP or TCP: both are already reliable, and a
second reliability layer is pure overhead.

### Wire format

`ReliablePacketCodec` encodes the layer's packets independently of MessagePack:
`type:1 | sn:2 | una:2 | contextLength:4 | messageLength:4` (little-endian), followed by the
context and message payloads. Truncated, malformed, or unknown-type packets throw
`InvalidDataException`.

## TCP vs KCP: when to choose which

- **TCP (`TcpTransport`)** — ordered byte stream with OS-managed congestion control. Choose it for
  RPC-heavy, latency-tolerant traffic and whenever simplicity matters. A single lost segment stalls
  *all* streams on the connection (head-of-line blocking).
- **KCP (`KcpTransport`)** — reliable ordered delivery over UDP with no-delay mode, aggressive fast
  retransmit (`FastResend = 2`), and congestion control disabled by default, trading fairness and
  CPU (10 ms update ticks) for latency. Choose it for real-time game traffic sharing a link with
  chatty RPCs, where one lost packet must not delay unrelated messages.

Both transports share the envelope contract-id serialization, so payload types registered on one
node work over either transport. They do not share fault payload types (each transport declares its
own private fault record), which is invisible to application code.

## Payload contract ids and unknown-contract behavior

Envelopes carry an `int` payload contract id instead of a type name; ids are the FNV-1a hash of the
payload type's `Type.FullName` (see `PayloadContractRegistry`). This bakes in one deployment
premise and defines what happens when it is violated.

### Deployment premise: every node loads the same contract assembly

All nodes in the cluster must load the same payload contract definitions (the same
`[SkynetActor]`-generated registration code, or explicit `PayloadContractRegistry.Register<T>()`
calls). A node that is missing a contract cannot deserialize incoming payloads of that type.

Because ids are derived from `Type.FullName`, payload types must be **concrete named types**.
Constructed generic types (`List<int>`, `Task<List<int>>`, ...) — including arrays of them
(`List<int>[]`) and non-generic types nested inside constructed generics — embed assembly-qualified
generic arguments with runtime version and public key in their full name, so two nodes on different
runtime versions or TFMs would hash *different* ids for the same payload. The registry rejects such
types at registration time (startup for generated contracts, first send for ad-hoc payloads) with
`PayloadContractTypeNotSupportedException`. Define an explicit named wrapper type for collections or
generic containers and register that type as the contract.

### Unknown contract id on the wire

When a node receives an envelope whose payload contract id is not registered:

- The frame is rejected and **the connection/session stays alive**; healthy traffic continues.
- If the frame is a *request* (`CallType.Call`), the receiver answers with a fault response (its own
  transport's `RemoteCallFault`/`KcpRemoteCallFault`, same `MessageId`, `IsResponse = true`). The
  initiator's pending call then fails immediately with `RpcDispatchException` — at RTT speed, not
  after a disconnect or timeout. Fire-and-forget (`CallType.Send`) frames are just dropped: there is
  no pending call to fail fast on the peer.
- Frames that cannot be parsed at all (malformed envelope bytes, or payload bytes that do not fit
  the resolved type) are dropped without any reply. Fault responses are never answered either: a
  fault a node cannot read must not trigger another fault, or two nodes with mismatched contract
  sets would loop forever on each other's fault frames.
- Legacy wire versions (below 3) still tear the connection down so mixed-version clusters fail
  loudly instead of silently losing traffic.

## Third-party notice

`src/Skynet.Transport.Kcp/ThirdParty/kcp2k/` contains the vendored managed KCP core from
[kcp2k](https://github.com/MirrorNetworking/kcp2k), MIT licensed (see `LICENSE` and `NOTICE.md` in
that directory). Only nullable-annotation-neutral vendored sources are included; no kcp2k namespace
types are used outside the vendored folder.

## Testing

- `tests/Skynet.Core.Tests/TcpTransportTlsTests.cs` — optional TLS on `TcpTransport`: encrypted
  cross-node `CallAsync` roundtrip (self-signed in-memory certificate + trust callback), TLS/plaintext
  mismatch fail-fast in both directions within bounded time, and client-side certificate rejection
  (connection closed, logged on the server, observable at the caller).
- `tests/Skynet.Transport.Kcp.Tests/Reliable/ReliableQueueTests.cs` — windowing, ack/una, reorder,
  wraparound, fast resend, RTO adaptation, Karn sampling, dead-link terminal state, `Reset`, codec.
- `tests/Skynet.Transport.Kcp.Tests/Reliable/ReliableLossyPipeTests.cs` — two queues wired through
  a seeded fake channel with injectable drop/delay/reorder driven by a virtual clock: 5% and 20% drop
  end-to-end in-order delivery, plus a black-hole dead-link scenario.
- `tests/Skynet.Transport.Kcp.Tests/KcpTransportTests.cs` — cross-node `CallAsync`, fire-and-forget
  `SendAsync`, and source-generated RPC proxy roundtrips over `KcpTransport`, mirroring
  `TcpTransportTests`.

namespace Skynet.Net.Encryption;

/// <summary>
/// Context passed to the gate authentication callback. <see cref="SessionKey"/> carries the raw derived
/// client → gate direction key when encryption is enabled (null in plaintext mode) so deployments can
/// implement proof-of-possession or bind the key to application credentials. Callbacks must not persist
/// the key. Since the D-5 wire change the session uses two independent directional keys; the callback
/// receives the client → gate one (the same seed derives both, so it proves possession of the key material).
/// </summary>
public sealed record GateAuthenticationContext(SessionMetadata Metadata, ReadOnlyMemory<byte>? SessionKey);

namespace Skynet.Net.Encryption;

/// <summary>
/// Context passed to the gate authentication callback. <see cref="SessionKey"/> carries the raw derived
/// session key when encryption is enabled (null in plaintext mode) so deployments can implement
/// proof-of-possession or bind the key to application credentials. Callbacks must not persist the key.
/// </summary>
public sealed record GateAuthenticationContext(SessionMetadata Metadata, ReadOnlyMemory<byte>? SessionKey);

namespace Skynet.Net.Encryption;

/// <summary>
/// Result of processing one pre-handshake frame: either an outbound handshake response to relay to the
/// client, the derived directional session keys after a successful ConfirmEncryptKey, or both (none for a
/// token request, keys only for a confirm). <see cref="Response"/> is the exact frame bytes to send.
/// </summary>
internal sealed record GateHandshakeStep(byte[]? Response, byte[]? ClientToGateKey, byte[]? GateToClientKey);

using MessagePack;

namespace Skynet.Core.RpcMessages;

/// <summary>
/// Placeholder payload used by generated RPC proxies for contract methods without parameters.
/// </summary>
[MessagePackObject]
public sealed class EmptyPayload
{
}

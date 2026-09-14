namespace Skynet.Transport.Kcp.Serialization;

// Do not delete: without this attributed resolver the MessagePack source generator emits an
// implicit internal GeneratedMessagePackResolver into this assembly, which collides (CS0436)
// with the resolver generated into Skynet.Transport.Kcp.Tests — visible there through this
// assembly's InternalsVisibleTo. Mirrors the explicit resolvers of Skynet.Core/Cluster.
[MessagePack.GeneratedMessagePackResolver]
internal partial class SkynetTransportKcpMessagePackResolver
{
}

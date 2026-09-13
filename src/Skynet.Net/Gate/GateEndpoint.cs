namespace Skynet.Net;

/// <summary>
/// Identifies a single outbound Gate endpoint by a stable name and its address ("host:port").
/// The name is the pool key: re-adding or updating an endpoint with the same name and a changed
/// address replaces the stale connection instead of opening a duplicate one.
/// </summary>
public sealed record GateEndpoint(string Name, string Address)
{
	public GateEndpoint(string name)
		: this(name, name)
	{
	}
}

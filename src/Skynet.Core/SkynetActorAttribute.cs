namespace Skynet.Core;

[AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = false)]
public sealed class SkynetActorAttribute(string? name = null) : Attribute
{
	public string? Name { get; } = name;

	public bool Unique { get; set; }
}

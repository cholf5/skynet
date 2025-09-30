using System;

namespace Skynet.Core;

[AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = false)]
public sealed class SkynetActorAttribute : Attribute
{
	public SkynetActorAttribute(string? name = null)
	{
	Name = name;
	}

	public string? Name { get; }

	public bool Unique { get; set; }
}

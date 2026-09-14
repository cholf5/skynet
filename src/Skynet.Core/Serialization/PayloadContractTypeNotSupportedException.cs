using System.Globalization;

namespace Skynet.Core.Serialization;

/// <summary>
/// Thrown when a payload type is registered whose <see cref="Type.FullName"/> embeds
/// assembly-qualified generic arguments (constructed generic types, arrays of them, or types
/// nested inside them). Such full names hash to different contract ids on nodes that run different
/// runtime versions or target frameworks, silently splitting the cluster into partitions that
/// cannot read each other's payloads.
/// </summary>
public sealed class PayloadContractTypeNotSupportedException : InvalidOperationException
{
	private const string DefaultMessageTemplate =
		"Payload type '{0}' cannot be used as a payload contract: its Type.FullName embeds " +
		"assembly-qualified generic arguments (e.g. 'List`1[[System.Int32, System.Private.CoreLib, " +
		"Version=...]]'), which hash to different contract ids on nodes running different runtime " +
		"versions or target frameworks. Payloads must be concrete named types; define an explicit " +
		"wrapper type for collections or generic containers and register that named type as the contract.";

	public PayloadContractTypeNotSupportedException(string payloadTypeFullName)
		: base(string.Format(CultureInfo.InvariantCulture, DefaultMessageTemplate, payloadTypeFullName))
	{
		PayloadTypeFullName = payloadTypeFullName;
	}

	/// <summary>Gets the full name of the rejected payload type.</summary>
	public string PayloadTypeFullName { get; }
}

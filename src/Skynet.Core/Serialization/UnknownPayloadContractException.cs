using System.Globalization;

namespace Skynet.Core.Serialization;

/// <summary>
/// Thrown when an envelope carries a payload contract id that is not registered on the receiving
/// node. This replaces runtime string-to-type reflection for payload resolution: the payload can
/// never be recovered by guessing types from the wire.
/// </summary>
public sealed class UnknownPayloadContractException : InvalidOperationException
{
	private const string DefaultMessageTemplate =
		"Unable to resolve payload contract id {0}: the type is not registered on this node. " +
		"Ensure every receiving node registers the payload type via PayloadContractRegistry.Register<T>() " +
		"or generates it through [SkynetActor] contracts, and that all nodes share the same contract definitions.";

	public UnknownPayloadContractException(int contractId)
		: base(string.Format(CultureInfo.InvariantCulture, DefaultMessageTemplate, contractId))
	{
		ContractId = contractId;
	}

	/// <summary>
	/// Gets the contract id that failed to resolve.
	/// </summary>
	public int ContractId { get; }
}

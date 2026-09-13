using System.Text;
using FluentAssertions;
using Skynet.Core.Serialization;
using Xunit;

namespace Skynet.Core.Tests;

public sealed class PayloadContractRegistryTests
{
	[Fact]
	public void ComputeContractId_ShouldBeStableForKnownFullName()
	{
		// FNV-1a 32-bit over the UTF-8 bytes of the full name. The literal was computed with an
		// independent implementation, so any accidental change of the algorithm fails here and
		// would break cross-node contract id agreement.
		PayloadContractRegistry.ComputeContractId("Skynet.Core.Tests.PayloadContractSample")
			.Should().Be(1635710109);
	}

	[Fact]
	public void ComputeContractId_ShouldMatchReferenceImplementation()
	{
		const string fullName = "System.String";
		uint hash = 2166136261;
		foreach (var value in Encoding.UTF8.GetBytes(fullName))
		{
			hash ^= value;
			hash *= 16777619;
		}

		PayloadContractRegistry.ComputeContractId(fullName).Should().Be(unchecked((int)hash));
	}

	[Fact]
	public void ComputeContractId_ShouldNeverReturnReservedNullId()
	{
		var names = new[]
		{
			"Skynet.Core.Tests.PayloadContractSample",
			"Skynet.Core.RpcMessages.EmptyPayload",
			"System.String",
			"Skynet.Cluster.RemoteCallFault"
		};

		foreach (var name in names)
		{
			PayloadContractRegistry.ComputeContractId(name).Should().NotBe(PayloadContractRegistry.NullPayloadContractId);
		}
	}

	[Fact]
	public void Register_ShouldBeIdempotentForSameType()
	{
		PayloadContractRegistry.Register<PayloadContractSample>();
		PayloadContractRegistry.Register<PayloadContractSample>();

		PayloadContractRegistry.TryGetContractId(typeof(PayloadContractSample), out var id).Should().BeTrue();
		id.Should().Be(PayloadContractRegistry.ComputeContractId("Skynet.Core.Tests.PayloadContractSample"));
		PayloadContractRegistry.TryResolve(id, out var resolved).Should().BeTrue();
		resolved.Should().Be(typeof(PayloadContractSample));
	}

	[Fact]
	public void GetOrRegister_ShouldReturnSameIdForSameType()
	{
		var first = PayloadContractRegistry.GetOrRegister(typeof(PayloadContractSample));
		var second = PayloadContractRegistry.GetOrRegister(typeof(PayloadContractSample));

		second.Should().Be(first);
		first.Should().Be(PayloadContractRegistry.ComputeContractId("Skynet.Core.Tests.PayloadContractSample"));
	}

	[Fact]
	public void Register_ShouldThrowOnContractIdConflict()
	{
		PayloadContractRegistry.Register(typeof(PayloadContractConflict), 123456789);

		var act = () => PayloadContractRegistry.Register(typeof(PayloadContractSample), 123456789);

		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*123456789*PayloadContractConflict*PayloadContractSample*");
	}

	[Fact]
	public void Register_ShouldBeIdempotentWhenSameTypeRegistersTwiceWithExplicitId()
	{
		PayloadContractRegistry.Register(typeof(PayloadContractExplicitId), 987654321);
		var act = () => PayloadContractRegistry.Register(typeof(PayloadContractExplicitId), 987654321);

		act.Should().NotThrow();
	}

	[Fact]
	public void TryResolve_ShouldReturnFalseForReservedNullId()
	{
		PayloadContractRegistry.TryResolve(PayloadContractRegistry.NullPayloadContractId, out _).Should().BeFalse();
	}

	[Fact]
	public void CommonPrimitives_ShouldBePreRegistered()
	{
		PayloadContractRegistry.TryResolve(PayloadContractRegistry.ComputeContractId("System.String"), out var stringType)
			.Should().BeTrue();
		stringType.Should().Be(typeof(string));

		PayloadContractRegistry.TryResolve(PayloadContractRegistry.ComputeContractId("System.Int32"), out var intType)
			.Should().BeTrue();
		intType.Should().Be(typeof(int));
	}
}

internal sealed record PayloadContractSample;

internal sealed record PayloadContractConflict;

internal sealed record PayloadContractExplicitId;

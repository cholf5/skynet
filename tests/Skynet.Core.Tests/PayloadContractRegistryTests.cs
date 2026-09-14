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

	[Fact]
	public void ConstructedGenericFullName_ShouldEmbedAssemblyQualifiedArguments()
	{
		// Fixates the researched .NET behavior that motivates the rejection rule below: constructed
		// generic types embed assembly-qualified argument names (with runtime version and public
		// key) even when the argument lives in the payload's own assembly. Two nodes on different
		// runtime versions or TFMs therefore hash different contract ids for the same payload.
		typeof(List<int>).FullName.Should().Contain("System.Int32, System.Private.CoreLib, Version=");
		typeof(GenericPayloadWrapper<PayloadContractSample>).FullName.Should().Contain("Version=");
	}

	[Fact]
	public void Register_ShouldRejectConstructedGenericPayload()
	{
		var act = () => PayloadContractRegistry.Register(typeof(List<int>));

		act.Should().Throw<PayloadContractTypeNotSupportedException>()
			.Where(exception => exception.PayloadTypeFullName.Contains("System.Collections.Generic.List`1",
				StringComparison.Ordinal))
			.Where(exception => exception.Message.Contains("wrapper type", StringComparison.OrdinalIgnoreCase),
				"the error must tell the user to define an explicit wrapper type");
	}

	[Fact]
	public void Register_ShouldRejectArrayOfConstructedGenericPayload()
	{
		// The array type itself is not generic, but its full name embeds the element's generic
		// arguments and drifts the same way.
		var act = () => PayloadContractRegistry.Register(typeof(List<int>[]));

		act.Should().Throw<PayloadContractTypeNotSupportedException>();
	}

	[Fact]
	public void Register_ShouldRejectTypeNestedInConstructedGeneric()
	{
		var act = () => PayloadContractRegistry.Register(typeof(GenericPayloadHost<PayloadContractSample>.Payload));

		act.Should().Throw<PayloadContractTypeNotSupportedException>();
	}

	[Fact]
	public void GetOrRegister_ShouldRejectConstructedGenericPayload()
	{
		var act = () => PayloadContractRegistry.GetOrRegister(typeof(Dictionary<string, int>));

		act.Should().Throw<PayloadContractTypeNotSupportedException>()
			.Where(exception => exception.PayloadTypeFullName.Contains(
				"System.Collections.Generic.Dictionary`2", StringComparison.Ordinal));
	}

	[Fact]
	public void Register_ShouldAllowLegalNamedPayloads()
	{
		// The rule must not over-reject: arrays of named types, enums, and nested types of named
		// types have full names that never embed assembly-qualified generic arguments.
		PayloadContractRegistry.Register<PayloadContractSampleEnum>();
		PayloadContractRegistry.Register<PayloadContractSample[]>();
		PayloadContractRegistry.Register<PayloadContractSampleHost.PayloadContractSampleNested>();

		PayloadContractRegistry.TryResolve(
			PayloadContractRegistry.ComputeContractId("Skynet.Core.Tests.PayloadContractSampleEnum"),
			out var enumType).Should().BeTrue();
		enumType.Should().Be(typeof(PayloadContractSampleEnum));
		PayloadContractRegistry.TryResolve(
			PayloadContractRegistry.ComputeContractId("Skynet.Core.Tests.PayloadContractSample[]"),
			out var arrayType).Should().BeTrue();
		arrayType.Should().Be(typeof(PayloadContractSample[]));
	}
}

internal sealed record PayloadContractSample;

internal sealed record PayloadContractConflict;

internal sealed record PayloadContractExplicitId;

internal enum PayloadContractSampleEnum
{
	None
}

internal sealed class GenericPayloadWrapper<T>
{
}

internal sealed class GenericPayloadHost<T>
{
	internal sealed class Payload
	{
	}
}

internal static class PayloadContractSampleHost
{
	internal sealed class PayloadContractSampleNested
	{
	}
}

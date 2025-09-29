using FluentAssertions;
using Skynet.Extras;
using Xunit;

namespace Skynet.Extras.Tests
{
	public sealed class AssemblySmokeTests
	{
		[Fact]
		public void ShouldExposeAssemblyMarker()
		{
			typeof(ExtrasAssembly).Should().NotBeNull();
		}
	}
}

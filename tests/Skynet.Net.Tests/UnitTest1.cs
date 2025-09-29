using FluentAssertions;
using Skynet.Net;
using Xunit;

namespace Skynet.Net.Tests
{
	public sealed class AssemblySmokeTests
	{
		[Fact]
		public void ShouldExposeAssemblyMarker()
		{
			typeof(NetAssembly).Should().NotBeNull();
		}
	}
}

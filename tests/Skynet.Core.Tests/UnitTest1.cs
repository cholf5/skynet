using FluentAssertions;
using Skynet.Core;
using Xunit;

namespace Skynet.Core.Tests
{
	public sealed class AssemblySmokeTests
	{
		[Fact]
		public void ShouldExposeAssemblyMarker()
		{
			typeof(CoreAssembly).Should().NotBeNull();
		}
	}
}

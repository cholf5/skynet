using FluentAssertions;
using Skynet.Cluster;
using Xunit;

namespace Skynet.Cluster.Tests
{
	public sealed class AssemblySmokeTests
	{
		[Fact]
		public void ShouldExposeAssemblyMarker()
		{
			typeof(ClusterAssembly).Should().NotBeNull();
		}
	}
}

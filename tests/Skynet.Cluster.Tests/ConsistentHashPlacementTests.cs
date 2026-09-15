using System.Collections.Concurrent;
using FluentAssertions;
using Skynet.Cluster;

namespace Skynet.Cluster.Tests;

public sealed class ConsistentHashPlacementTests
{
	private const int KeyCount = 1000;

	[Fact]
	public void PlaceShouldReturnStableNodeForRepeatedKeys()
	{
		var placement = new ConsistentHashPlacement(["node-a", "node-b", "node-c", "node-d"]);

		placement.Nodes.Should().Equal("node-a", "node-b", "node-c", "node-d");

		var firstPass = new string[KeyCount];
		for (var i = 0; i < KeyCount; i++)
		{
			firstPass[i] = placement.Place($"room:{i}");
			firstPass[i].Should().NotBeNull();
			placement.Nodes.Should().Contain(firstPass[i]);
		}

		for (var i = 0; i < KeyCount; i++)
		{
			placement.Place($"room:{i}").Should().Be(firstPass[i]);
		}
	}

	[Fact]
	public void IndependentInstancesWithSameNodesShouldProduceIdenticalMappings()
	{
		// The ring is derived from the node set only, so two processes (or two instances built
		// from enumeration orders that differ) must agree on every key without coordination.
		var placementA = new ConsistentHashPlacement(["node-a", "node-b", "node-c", "node-d"]);
		var placementB = new ConsistentHashPlacement(["node-d", "node-c", "node-b", "node-a"]);

		for (var i = 0; i < KeyCount; i++)
		{
			var key = $"room:{i}";
			placementA.Place(key).Should().Be(placementB.Place(key));
		}
	}

	[Fact]
	public void RemovingAQuarterOfTheNodesShouldMigrateAboutOneQuarterOfTheKeys()
	{
		var before = BuildMapping(4);
		before.Placement.Nodes.Should().Equal("node-0", "node-1", "node-2", "node-3");

		before.Placement.UpdateNodes(["node-0", "node-1", "node-2"]);

		var remaining = new HashSet<string> { "node-0", "node-1", "node-2" };
		var migrated = 0;
		for (var i = 0; i < KeyCount * 4; i++)
		{
			var key = $"room:{i}";
			var target = before.Placement.Place(key);
			remaining.Should().Contain(target);
			if (target != before.Mappings[i])
			{
				migrated++;
			}
		}

		// Expected ~25% with a ±10% tolerance band; 4000 keys keep the binomial noise far away
		// from the band edges, and the hash is deterministic so the ratio is fixed per build.
		var migratedRatio = migrated / (double)(KeyCount * 4);
		migratedRatio.Should().BeGreaterThan(0.15).And.BeLessThan(0.35);
	}

	[Fact]
	public void AddingANodeShouldMigrateAboutOneFifthOfTheKeys()
	{
		var before = BuildMapping(4);
		var allNodes = new[] { "node-0", "node-1", "node-2", "node-3", "node-4" };

		before.Placement.UpdateNodes(allNodes);

		var migrated = 0;
		var after = new HashSet<string>();
		for (var i = 0; i < KeyCount * 4; i++)
		{
			var key = $"room:{i}";
			var target = before.Placement.Place(key);
			after.Add(target);
			if (target != before.Mappings[i])
			{
				migrated++;
			}
		}

		after.Should().BeEquivalentTo(allNodes);
		var migratedRatio = migrated / (double)(KeyCount * 4);
		migratedRatio.Should().BeGreaterThan(0.10).And.BeLessThan(0.30);
	}

	[Fact]
	public void KeysShouldSpreadAcrossAllNodes()
	{
		var mapping = new Dictionary<string, int>();
		var placement = new ConsistentHashPlacement(["node-a", "node-b", "node-c", "node-d"]);
		var total = KeyCount * 4;
		for (var i = 0; i < total; i++)
		{
			var target = placement.Place($"room:{i}");
			mapping[target] = mapping.GetValueOrDefault(target) + 1;
		}

		mapping.Should().HaveCount(4);
		// With 160 virtual nodes per node the share of each node stays close to 25%;
		// these bounds only catch gross distribution defects, not statistical noise.
		foreach (var share in mapping.Values)
		{
			(share / (double)total).Should().BeGreaterThan(0.15).And.BeLessThan(0.35);
		}
	}

	[Fact]
	public void UpdatingToTheSameNodeSetShouldKeepMappingsStable()
	{
		var before = BuildMapping(4);

		before.Placement.UpdateNodes(["node-0", "node-1", "node-2", "node-3"]);

		for (var i = 0; i < KeyCount; i++)
		{
			before.Placement.Place($"room:{i}").Should().Be(before.Mappings[i]);
		}
	}

	[Fact]
	public void InvalidNodeSetsShouldBeRejectedWithoutChangingTheRing()
	{
		var placement = new ConsistentHashPlacement(["node-a", "node-b"]);
		var mappings = new string[KeyCount];
		for (var i = 0; i < KeyCount; i++)
		{
			mappings[i] = placement.Place($"room:{i}");
		}

		FluentActions.Invoking(() => new ConsistentHashPlacement(Array.Empty<string>()))
			.Should().Throw<ArgumentException>();
		FluentActions.Invoking(() => new ConsistentHashPlacement(null!))
			.Should().Throw<ArgumentNullException>();
		FluentActions.Invoking(() => new ConsistentHashPlacement(["node-a", null!]))
			.Should().Throw<ArgumentException>();
		FluentActions.Invoking(() => new ConsistentHashPlacement(["node-a", ""]))
			.Should().Throw<ArgumentException>();
		FluentActions.Invoking(() => placement.UpdateNodes(Array.Empty<string>()))
			.Should().Throw<ArgumentException>();
		FluentActions.Invoking(() => placement.UpdateNodes(null!))
			.Should().Throw<ArgumentNullException>();
		FluentActions.Invoking(() => placement.UpdateNodes(["node-a", null!]))
			.Should().Throw<ArgumentException>();

		// Duplicates are collapsed rather than rejected.
		var duplicated = new ConsistentHashPlacement(["node-a", "node-a", "node-b"]);
		duplicated.Nodes.Should().Equal("node-a", "node-b");

		// A rejected update must leave the current ring untouched.
		for (var i = 0; i < KeyCount; i++)
		{
			placement.Place($"room:{i}").Should().Be(mappings[i]);
		}
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	[InlineData(10001)]
	public void InvalidVirtualNodeCountsShouldBeRejected(int virtualNodeCount)
	{
		FluentActions.Invoking(() => new ConsistentHashPlacement(["node-a"], virtualNodeCount))
			.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public void SingleVirtualNodePerNodeShouldBeAccepted()
	{
		var placement = new ConsistentHashPlacement(["node-a", "node-b"], 1);

		placement.Place("room:1").Should().BeOneOf("node-a", "node-b");
		placement.Place("room:1").Should().Be(placement.Place("room:1"));
	}

	[Fact]
	public void PlaceShouldRejectNullOrEmptyKeys()
	{
		var placement = new ConsistentHashPlacement(["node-a"]);

		FluentActions.Invoking(() => placement.Place(null!))
			.Should().Throw<ArgumentNullException>();
		FluentActions.Invoking(() => placement.Place(string.Empty))
			.Should().Throw<ArgumentException>();
	}

	[Fact]
	public async Task ConcurrentUpdatesAndLookupsShouldAlwaysResolveToKnownNodes()
	{
		var universe = Enumerable.Range(0, 8).Select(i => $"node-{i}").ToArray();
		var universeSet = universe.ToHashSet();
		var placement = new ConsistentHashPlacement(universe);
		IReadOnlyCollection<string>[] nodeSets =
		[
			universe,
			universe.Take(5).ToArray(),
			universe.Skip(3).ToArray(),
			[universe[0], universe[6], universe[7]],
		];

		using var startGate = new ManualResetEventSlim(false);
		var errors = new ConcurrentBag<Exception>();

		var readerTasks = Enumerable.Range(0, 4)
			.Select(_ => Task.Run(() =>
			{
				startGate.Wait();
				try
				{
					for (var i = 0; i < 25000; i++)
					{
						var node = placement.Place($"room:{i % 997}");
						if (!universeSet.Contains(node))
						{
							throw new InvalidOperationException($"Resolved unknown node '{node}'.");
						}
					}
				}
				catch (Exception ex)
				{
					errors.Add(ex);
				}
			}))
			.ToArray();

		var writerTasks = Enumerable.Range(0, 2)
			.Select(writerIndex => Task.Run(() =>
			{
				startGate.Wait();
				try
				{
					for (var round = 0; round < 200; round++)
					{
						placement.UpdateNodes(nodeSets[(writerIndex + round) % nodeSets.Length]);
					}
				}
				catch (Exception ex)
				{
					errors.Add(ex);
				}
			}))
			.ToArray();

		startGate.Set();
		await Task.WhenAll(readerTasks.Concat(writerTasks));

		errors.Should().BeEmpty();

		// After the race settles, the current node set is a non-empty subset of the universe
		// and 1000 keys resolve into it consistently across a repeated pass.
		var currentNodes = placement.Nodes;
		currentNodes.Should().NotBeEmpty();
		currentNodes.Should().OnlyContain(node => universeSet.Contains(node));

		var resolved = new string[KeyCount];
		for (var i = 0; i < KeyCount; i++)
		{
			resolved[i] = placement.Place($"room:{i}");
			currentNodes.Should().Contain(resolved[i]);
		}

		for (var i = 0; i < KeyCount; i++)
		{
			placement.Place($"room:{i}").Should().Be(resolved[i]);
		}
	}

	private static (ConsistentHashPlacement Placement, string[] Mappings) BuildMapping(int nodeCount)
	{
		var nodes = Enumerable.Range(0, nodeCount).Select(i => $"node-{i}").ToArray();
		var placement = new ConsistentHashPlacement(nodes);
		var mappings = new string[KeyCount * 4];
		for (var i = 0; i < mappings.Length; i++)
		{
			mappings[i] = placement.Place($"room:{i}");
		}

		return (placement, mappings);
	}
}

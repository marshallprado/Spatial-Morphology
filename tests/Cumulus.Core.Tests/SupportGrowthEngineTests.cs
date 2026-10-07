using System.Collections.Generic;
using NUnit.Framework;

namespace Cumulus.Tests;

public class SupportGrowthEngineTests
{
    [Test]
    public void RoutesThroughUnassignedCellsToGroundedProgramUsingFaceNeighbours()
    {
        var keys = new List<VoxelKey>
        {
            new VoxelKey(0, 0, 0), // grounded program anchor
            new VoxelKey(1, 0, 0),
            new VoxelKey(1, 0, 1),
            new VoxelKey(1, 0, 2)  // floating program
        };
        var programs = new List<int> { 0, -1, -1, 1 };

        var result = SupportGrowthEngine.Run(new SupportGrowthRequest(
            keys, programs, new List<double> { 0, 1, 1, 0 },
            new List<int>(), 2, horizontalStepPenalty: 2.0, analysisPenalty: 1.0));

        Assert.That(result.FloatingComponentCount, Is.EqualTo(1));
        Assert.That(result.ResolvedComponentCount, Is.EqualTo(1));
        Assert.That(result.UnresolvedComponents, Is.Empty);
        Assert.That(result.AddedSupportIndices, Is.EquivalentTo(new[] { 1, 2 }));
        Assert.That(result.ProgramIndices[1], Is.EqualTo(2));
        Assert.That(result.ProgramIndices[2], Is.EqualTo(2));
    }

    [Test]
    public void ReportsFloatingComponentWhenNoUnassignedRouteExists()
    {
        var keys = new List<VoxelKey>
        {
            new VoxelKey(0, 0, 0),
            new VoxelKey(0, 0, 1)
        };

        var result = SupportGrowthEngine.Run(new SupportGrowthRequest(
            keys, new List<int> { 0, 1 }, new List<double> { 0, 0 },
            new List<int>(), 2, 2.0, 1.0));

        Assert.That(result.AddedSupportIndices, Is.Empty);
        Assert.That(result.UnresolvedComponents, Has.Count.EqualTo(1));
        Assert.That(result.UnresolvedComponents[0], Is.EquivalentTo(new[] { 1 }));
    }

    [Test]
    public void TreatsCoreVoxelAsTrustedAnchor()
    {
        var keys = new List<VoxelKey>
        {
            new VoxelKey(0, 0, 0),
            new VoxelKey(1, 0, 1),
            new VoxelKey(2, 0, 1)
        };

        var result = SupportGrowthEngine.Run(new SupportGrowthRequest(
            keys, new List<int> { -2, -1, 0 }, new List<double> { 0, 1, 0 },
            new List<int>(), 1, 2.0, 1.0));

        Assert.That(result.UnresolvedComponents, Is.Empty);
        Assert.That(result.AddedSupportIndices, Is.EquivalentTo(new[] { 1 }));
    }
}

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Cumulus.Tests
{
    [TestFixture]
    public class StructureOptimizationEngineTests
    {
        [Test]
        public void ConnectsFloatingProgramToGroundWithDownwardStructure()
        {
            var keys = new List<StructureVoxelKey>
            {
                new StructureVoxelKey(0, 0, 0),
                new StructureVoxelKey(0, 0, 1),
                new StructureVoxelKey(0, 0, 2)
            };

            var result = StructureOptimizationEngine.Run(
                new StructureOptimizationRequest(
                    keys, new[] { -1, -1, 0 }, new[] { 0 }, 0.25, 75, 4));

            Assert.That(result.StructureIndices, Is.EquivalentTo(new[] { 0, 1 }));
            Assert.That(result.UnsupportedIndices, Is.Empty);
            Assert.That(result.VoxelCapacity[2], Is.EqualTo(1.0));
            Assert.That(result.ClusterPaths[0], Has.Count.EqualTo(1));
            Assert.That(result.ClusterPaths[0][0], Is.EqualTo(new[] { 2, 1, 0 }));
        }

        [Test]
        public void FloatingCoreIsTransmissiveButNotAGroundedRoot()
        {
            var keys = new List<StructureVoxelKey>
            {
                new StructureVoxelKey(0, 0, 1),
                new StructureVoxelKey(0, 0, 2)
            };

            var result = StructureOptimizationEngine.Run(
                new StructureOptimizationRequest(
                    keys, new[] { -2, 0 }, new int[0], 0.25, 75, 4));

            Assert.That(result.StructureIndices, Is.Empty);
            Assert.That(result.UnsupportedIndices, Is.EquivalentTo(new[] { 0, 1 }));
        }

        [Test]
        public void HighProgramPrefersNearbyGroundedCoreOverLongColumn()
        {
            var keys = new List<StructureVoxelKey>();
            var assignments = new List<int>();

            // Grounded core column at x=0.
            for (int z = 0; z <= 6; z++)
            {
                keys.Add(new StructureVoxelKey(0, 0, z));
                assignments.Add(-2);
            }

            // A high program voxel one cell beside the core, plus a possible
            // direct unallocated column beneath it.
            for (int z = 0; z < 6; z++)
            {
                keys.Add(new StructureVoxelKey(1, 0, z));
                assignments.Add(-1);
            }

            int programIndex = keys.Count;
            keys.Add(new StructureVoxelKey(1, 0, 6));
            assignments.Add(0);

            var result = StructureOptimizationEngine.Run(
                new StructureOptimizationRequest(
                    keys, assignments, new[] { 0, 7 }, 0.25, 75, 4));

            var path = result.ClusterPaths[0][0];

            Assert.That(result.UnsupportedIndices, Is.Empty);
            Assert.That(path[0], Is.EqualTo(programIndex));
            Assert.That(path, Does.Contain(6)); // nearest core at the same elevation
            Assert.That(path[path.Count - 1], Is.EqualTo(0)); // complete route to ground
            Assert.That(result.StructureIndices, Is.Empty);
        }

        [Test]
        public void DoesNotSupportProgramFromAbove()
        {
            var keys = new List<StructureVoxelKey>
            {
                new StructureVoxelKey(0, 0, 1), // program source
                new StructureVoxelKey(0, 0, 2), // core above source
                new StructureVoxelKey(1, 0, 2),
                new StructureVoxelKey(2, 0, 2),
                new StructureVoxelKey(2, 0, 1),
                new StructureVoxelKey(2, 0, 0)
            };

            var result = StructureOptimizationEngine.Run(
                new StructureOptimizationRequest(
                    keys, new[] { 0, -2, -1, -1, -1, -1 },
                    new[] { 5 }, 0.25, 75, 4));

            Assert.That(result.UnsupportedIndices, Does.Contain(0));
            Assert.That(result.ClusterPaths[0], Is.Empty);
        }

        [Test]
        public void EnforcesMaximumCantileverRun()
        {
            var keys = new List<StructureVoxelKey>
            {
                new StructureVoxelKey(0, 0, 2), // program
                new StructureVoxelKey(1, 0, 2),
                new StructureVoxelKey(2, 0, 2),
                new StructureVoxelKey(2, 0, 1),
                new StructureVoxelKey(2, 0, 0)
            };

            var result = StructureOptimizationEngine.Run(
                new StructureOptimizationRequest(
                    keys, new[] { 0, -1, -1, -1, -1 },
                    new[] { 4 }, 0.25, 75, 1));

            Assert.That(result.StructureIndices, Is.Empty);
            Assert.That(result.UnsupportedIndices, Does.Contain(0));
        }

        [Test]
        public void OutputsOnePathForEveryProgramVoxelInCluster()
        {
            var keys = new List<StructureVoxelKey>
            {
                new StructureVoxelKey(0, 0, 0),
                new StructureVoxelKey(0, 0, 1),
                new StructureVoxelKey(0, 0, 2),
                new StructureVoxelKey(1, 0, 2)
            };

            var result = StructureOptimizationEngine.Run(
                new StructureOptimizationRequest(
                    keys, new[] { -1, -1, 0, 0 }, new[] { 0 }, 0.25, 75, 4));

            Assert.That(result.ClusterPaths, Has.Count.EqualTo(1));
            Assert.That(result.ClusterPaths[0], Has.Count.EqualTo(2));
            Assert.That(result.ClusterPaths[0].All(path => path.Last() == 0), Is.True);
        }
    }
}

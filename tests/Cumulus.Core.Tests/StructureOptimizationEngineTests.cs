using System.Collections.Generic;
using NUnit.Framework;

namespace Cumulus.Tests
{
    [TestFixture]
    public class StructureOptimizationEngineTests
    {
        [Test]
        public void ConnectsFloatingProgramToGroundWithFullDownwardCapacity()
        {
            var keys = new List<StructureVoxelKey>
            {
                new StructureVoxelKey(0, 0, 0),
                new StructureVoxelKey(0, 0, 1),
                new StructureVoxelKey(0, 0, 2)
            };
            var result = StructureOptimizationEngine.Run(
                new StructureOptimizationRequest(keys, new[] { -1, -1, 0 }, new[] { 0 }, 0.25, 75));

            Assert.That(result.StructureIndices, Is.EquivalentTo(new[] { 0, 1 }));
            Assert.That(result.UnsupportedIndices, Is.Empty);
            Assert.That(result.VoxelCapacity[2], Is.EqualTo(1.0));
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
                new StructureOptimizationRequest(keys, new[] { -2, 0 }, new int[0], 0.25, 75));

            Assert.That(result.StructureIndices, Is.Empty);
            Assert.That(result.UnsupportedIndices, Is.EquivalentTo(new[] { 0, 1 }));
        }

        [Test]
        public void HorizontalTransferReducesProgramCapacity()
        {
            var keys = new List<StructureVoxelKey>
            {
                new StructureVoxelKey(0, 0, 0),
                new StructureVoxelKey(0, 0, 1),
                new StructureVoxelKey(1, 0, 1)
            };
            var result = StructureOptimizationEngine.Run(
                new StructureOptimizationRequest(keys, new[] { -1, -1, 0 }, new[] { 0 }, 0.25, 75));

            Assert.That(result.VoxelCapacity[2], Is.EqualTo(0.25));
            Assert.That(result.WeakClusterIndices, Is.EqualTo(new[] { 0 }));
        }

        [Test]
        public void UpwardTransferHasLowerCapacityThanHorizontalTransfer()
        {
            // The source has no horizontal neighbour at its own elevation.
            // It must move up, move horizontally twice, then descend through
            // an offset grounded column.
            var keys = new List<StructureVoxelKey>
    {
        new StructureVoxelKey(0, 0, 1), // 0: source
        new StructureVoxelKey(0, 0, 2), // 1: upward step
        new StructureVoxelKey(1, 0, 2), // 2: horizontal step
        new StructureVoxelKey(2, 0, 2), // 3: horizontal step
        new StructureVoxelKey(2, 0, 1), // 4: downward step
        new StructureVoxelKey(2, 0, 0)  // 5: grounded voxel
    };

            var result = StructureOptimizationEngine.Run(
                new StructureOptimizationRequest(
                    keys,
                    new[] { 0, -1, -1, -1, -1, -1 },
                    new[] { 5 },
                    0.25,
                    75));

            // Up × horizontal × horizontal × down × down:
            // 0.125 × 0.25 × 0.25 × 1.0 × 1.0 = 0.0078125
            Assert.That(result.VoxelCapacity[0], Is.EqualTo(0.0078125));
            Assert.That(result.WeakClusterIndices, Is.EqualTo(new[] { 0 }));
        }

        [Test]
        public void ReusedGroundedTrunkAccumulatesMoreImportance()
        {
            var keys = new List<StructureVoxelKey>
            {
                new StructureVoxelKey(0, 0, 0),
                new StructureVoxelKey(0, 0, 1),
                new StructureVoxelKey(0, 0, 2),
                new StructureVoxelKey(1, 0, 2)
            };
            var result = StructureOptimizationEngine.Run(
                new StructureOptimizationRequest(keys, new[] { -1, -1, 0, 1 }, new[] { 0 }, 0.25, 75));

            Assert.That(result.UnsupportedIndices, Is.Empty);
            Assert.That(result.CandidateImportance[1], Is.GreaterThan(0.0));
            Assert.That(result.CandidateImportance[0], Is.GreaterThan(0.0));
        }
    }
}

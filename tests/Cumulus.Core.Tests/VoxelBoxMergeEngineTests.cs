using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Cumulus.Tests
{
    [TestFixture]
    public class VoxelBoxMergeEngineTests
    {
        [Test]
        public void MergesFilledTwoByTwoByTwoBlockIntoOneBox()
        {
            var keys = new List<VoxelBoxKey>();
            var programs = new List<int>();

            for (int x = 0; x < 2; x++)
                for (int y = 0; y < 2; y++)
                    for (int z = 0; z < 2; z++)
                    {
                        keys.Add(new VoxelBoxKey(x, y, z));
                        programs.Add(0);
                    }

            var result = VoxelBoxMergeEngine.Merge(keys, programs);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].ProgramIndex, Is.EqualTo(0));
            Assert.That(result[0].Width, Is.EqualTo(2));
            Assert.That(result[0].Depth, Is.EqualTo(2));
            Assert.That(result[0].Height, Is.EqualTo(2));
            Assert.That(result[0].VoxelCount, Is.EqualTo(8));
        }

        [Test]
        public void DoesNotMergeAcrossProgramBoundaryOrUnassignedGap()
        {
            var keys = new List<VoxelBoxKey>
            {
                new VoxelBoxKey(0, 0, 0),
                new VoxelBoxKey(1, 0, 0),
                new VoxelBoxKey(2, 0, 0)
            };
            var programs = new List<int> { 0, -1, 1 };

            var result = VoxelBoxMergeEngine.Merge(keys, programs);

            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result[0].VoxelCount, Is.EqualTo(1));
            Assert.That(result[1].VoxelCount, Is.EqualTo(1));
            Assert.That(result.Select(r => r.ProgramIndex),
                Is.EquivalentTo(new[] { 0, 1 }));
        }

        [Test]
        public void MergesCoreVoxelsAsTheirOwnGroup()
        {
            var keys = new List<VoxelBoxKey>
            {
                new VoxelBoxKey(0, 0, 0),
                new VoxelBoxKey(0, 0, 1),
                new VoxelBoxKey(1, 0, 0)
            };
            var programs = new List<int> { -2, -2, 0 };

            var result = VoxelBoxMergeEngine.Merge(keys, programs);

            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result.Single(r => r.ProgramIndex == -2).VoxelCount,
                Is.EqualTo(2));
            Assert.That(result.Single(r => r.ProgramIndex == 0).VoxelCount,
                Is.EqualTo(1));
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Cumulus.Tests
{
    [TestFixture]
    public class FloorPlateExtractionEngineTests
    {
        [Test]
        public void MergesSameProgramSameFloorIntoOneFootprintRegion()
        {
            var keys = new List<FloorPlateVoxelKey>
            {
                new FloorPlateVoxelKey(0, 0, 0),
                new FloorPlateVoxelKey(1, 0, 0),
                new FloorPlateVoxelKey(0, 1, 0),
                new FloorPlateVoxelKey(1, 1, 0)
            };

            var result = FloorPlateExtractionEngine.Run(
                new FloorPlateExtractionRequest(
                    keys,
                    new[] { 0, 0, 0, 0 },
                    new[] { 1, 1, 1, 1 },
                    includeCore: true,
                    structureProgramIndex: -1,
                    includeStructure: false));

            Assert.That(result.Regions, Has.Count.EqualTo(1));
            Assert.That(result.Regions[0].FootprintCells, Has.Count.EqualTo(4));
            Assert.That(result.Regions[0].SourceVoxelIndices, Is.EqualTo(new[] { 0, 1, 2, 3 }));
        }

        [Test]
        public void SplitsDifferentProgramsAndDisconnectedFootprints()
        {
            var keys = new List<FloorPlateVoxelKey>
            {
                new FloorPlateVoxelKey(0, 0, 0),
                new FloorPlateVoxelKey(2, 0, 0),
                new FloorPlateVoxelKey(3, 0, 0)
            };

            var result = FloorPlateExtractionEngine.Run(
                new FloorPlateExtractionRequest(
                    keys,
                    new[] { 0, 0, 1 },
                    new[] { 1, 1, 1 },
                    includeCore: true,
                    structureProgramIndex: -1,
                    includeStructure: false));

            Assert.That(result.Regions, Has.Count.EqualTo(3));
            Assert.That(result.Regions.Select(region => region.ProgramIndex),
                Is.EqualTo(new[] { 0, 0, 1 }));
        }

        [Test]
        public void CollapsesVerticalVoxelsOntoOneFloorFootprint()
        {
            var keys = new List<FloorPlateVoxelKey>
            {
                new FloorPlateVoxelKey(0, 0, 0),
                new FloorPlateVoxelKey(0, 0, 1),
                new FloorPlateVoxelKey(1, 0, 1)
            };

            var result = FloorPlateExtractionEngine.Run(
                new FloorPlateExtractionRequest(
                    keys,
                    new[] { 0, 0, 0 },
                    new[] { 1, 1, 1 },
                    includeCore: true,
                    structureProgramIndex: -1,
                    includeStructure: false));

            Assert.That(result.Regions, Has.Count.EqualTo(1));
            Assert.That(result.Regions[0].FootprintCells, Has.Count.EqualTo(2));
            Assert.That(result.Regions[0].SourceVoxelIndices, Is.EqualTo(new[] { 0, 1, 2 }));
            Assert.That(result.Regions[0].MinimumVoxelZ, Is.EqualTo(0));
        }

        [Test]
        public void ExcludesUnallocatedAndStructureByDefaultButIncludesCore()
        {
            var keys = new List<FloorPlateVoxelKey>
            {
                new FloorPlateVoxelKey(0, 0, 0),
                new FloorPlateVoxelKey(1, 0, 0),
                new FloorPlateVoxelKey(2, 0, 0),
                new FloorPlateVoxelKey(3, 0, 0)
            };

            var result = FloorPlateExtractionEngine.Run(
                new FloorPlateExtractionRequest(
                    keys,
                    new[] { -1, -2, 0, 1 },
                    new[] { 1, 1, 1, 1 },
                    includeCore: true,
                    structureProgramIndex: 1,
                    includeStructure: false));

            Assert.That(result.Regions.Select(region => region.ProgramIndex),
                Is.EqualTo(new[] { -2, 0 }));
            Assert.That(result.SkippedUnallocatedVoxelCount, Is.EqualTo(1));
            Assert.That(result.SkippedStructureVoxelCount, Is.EqualTo(1));
        }
    }
}

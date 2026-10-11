using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Cumulus.Tests
{
    [TestFixture]
    public class ProgramInterfaceExtractionEngineTests
    {
        [Test]
        public void CreatesOneFaceForTwoAdjacentPrograms()
        {
            var keys = new List<ProgramInterfaceVoxelKey>
            {
                new ProgramInterfaceVoxelKey(0, 0, 0),
                new ProgramInterfaceVoxelKey(1, 0, 0)
            };

            var result = ProgramInterfaceExtractionEngine.Run(
                new ProgramInterfaceExtractionRequest(
                    keys,
                    new[] { 0, 1 },
                    true));

            Assert.That(result.Faces, Has.Count.EqualTo(1));
            Assert.That(result.Faces[0].ProgramA, Is.EqualTo(0));
            Assert.That(result.Faces[0].ProgramB, Is.EqualTo(1));
            Assert.That(result.Faces[0].Axis, Is.EqualTo(0));
            Assert.That(result.Faces[0].Direction, Is.EqualTo(1));
        }

        [Test]
        public void DoesNotCreateFacesTowardUnallocatedVoxels()
        {
            var keys = new List<ProgramInterfaceVoxelKey>
            {
                new ProgramInterfaceVoxelKey(0, 0, 0),
                new ProgramInterfaceVoxelKey(1, 0, 0)
            };

            var result = ProgramInterfaceExtractionEngine.Run(
                new ProgramInterfaceExtractionRequest(
                    keys,
                    new[] { 0, -1 },
                    true));

            Assert.That(result.Faces, Is.Empty);
            Assert.That(result.IgnoredUnallocatedContacts, Is.EqualTo(1));
        }

        [Test]
        public void CreatesProgramToCoreFaceOnlyWhenCoreIsIncluded()
        {
            var keys = new List<ProgramInterfaceVoxelKey>
            {
                new ProgramInterfaceVoxelKey(0, 0, 0),
                new ProgramInterfaceVoxelKey(1, 0, 0)
            };

            var included = ProgramInterfaceExtractionEngine.Run(
                new ProgramInterfaceExtractionRequest(
                    keys,
                    new[] { 0, -2 },
                    true));

            var excluded = ProgramInterfaceExtractionEngine.Run(
                new ProgramInterfaceExtractionRequest(
                    keys,
                    new[] { 0, -2 },
                    false));

            Assert.That(included.Faces, Has.Count.EqualTo(1));
            Assert.That(included.Faces[0].ProgramA, Is.EqualTo(0));
            Assert.That(included.Faces[0].ProgramB, Is.EqualTo(-2));
            Assert.That(excluded.Faces, Is.Empty);
            Assert.That(excluded.IgnoredCoreContacts, Is.EqualTo(1));
        }

        [Test]
        public void AggregatesProgramPairAdjacency()
        {
            var keys = new List<ProgramInterfaceVoxelKey>
            {
                new ProgramInterfaceVoxelKey(0, 0, 0),
                new ProgramInterfaceVoxelKey(1, 0, 0),
                new ProgramInterfaceVoxelKey(0, 1, 0),
                new ProgramInterfaceVoxelKey(1, 1, 0)
            };

            var result = ProgramInterfaceExtractionEngine.Run(
                new ProgramInterfaceExtractionRequest(
                    keys,
                    new[] { 0, 1, 0, 1 },
                    true));

            Assert.That(result.Faces, Has.Count.EqualTo(2));
            Assert.That(result.Adjacencies, Has.Count.EqualTo(1));
            Assert.That(result.Adjacencies.Single().FaceCount, Is.EqualTo(2));
        }
    }
}

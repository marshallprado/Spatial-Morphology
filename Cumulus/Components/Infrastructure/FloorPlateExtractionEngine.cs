// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;

namespace Cumulus
{
    /// <summary>Integer voxel coordinate used by the floor-plate extractor.</summary>
    public struct FloorPlateVoxelKey : IEquatable<FloorPlateVoxelKey>
    {
        public int X { get; }
        public int Y { get; }
        public int Z { get; }

        public FloorPlateVoxelKey(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public bool Equals(FloorPlateVoxelKey other) =>
            X == other.X && Y == other.Y && Z == other.Z;

        public override bool Equals(object obj) =>
            obj is FloorPlateVoxelKey && Equals((FloorPlateVoxelKey)obj);

        public override int GetHashCode()
        {
            unchecked { return ((X * 397) ^ Y) * 397 ^ Z; }
        }
    }

    /// <summary>Integer XY footprint coordinate used by the floor-plate extractor.</summary>
    public struct FloorPlateCell : IEquatable<FloorPlateCell>, IComparable<FloorPlateCell>
    {
        public int X { get; }
        public int Y { get; }

        public FloorPlateCell(int x, int y)
        {
            X = x;
            Y = y;
        }

        public bool Equals(FloorPlateCell other) => X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is FloorPlateCell && Equals((FloorPlateCell)obj);
        public override int GetHashCode()
        {
            unchecked { return (X * 397) ^ Y; }
        }

        public int CompareTo(FloorPlateCell other)
        {
            int x = X.CompareTo(other.X);
            return x != 0 ? x : Y.CompareTo(other.Y);
        }
    }

    /// <summary>One connected program footprint on one architectural floor.</summary>
    public sealed class FloorPlateRegion
    {
        public int ProgramIndex { get; }
        public int FloorIndex { get; }
        public int MinimumVoxelZ { get; }
        public IReadOnlyList<FloorPlateCell> FootprintCells { get; }
        public IReadOnlyList<int> SourceVoxelIndices { get; }

        public FloorPlateRegion(
            int programIndex,
            int floorIndex,
            int minimumVoxelZ,
            IReadOnlyList<FloorPlateCell> footprintCells,
            IReadOnlyList<int> sourceVoxelIndices)
        {
            ProgramIndex = programIndex;
            FloorIndex = floorIndex;
            MinimumVoxelZ = minimumVoxelZ;
            FootprintCells = footprintCells ?? throw new ArgumentNullException(nameof(footprintCells));
            SourceVoxelIndices = sourceVoxelIndices ?? throw new ArgumentNullException(nameof(sourceVoxelIndices));
        }
    }

    /// <summary>Input data for deterministic program-floor footprint extraction.</summary>
    public sealed class FloorPlateExtractionRequest
    {
        public IReadOnlyList<FloorPlateVoxelKey> Keys { get; }
        public IReadOnlyList<int> ProgramIndices { get; }
        public IReadOnlyList<int> FloorIndices { get; }
        public bool IncludeCore { get; }
        public int StructureProgramIndex { get; }
        public bool IncludeStructure { get; }

        public FloorPlateExtractionRequest(
            IReadOnlyList<FloorPlateVoxelKey> keys,
            IReadOnlyList<int> programIndices,
            IReadOnlyList<int> floorIndices,
            bool includeCore,
            int structureProgramIndex,
            bool includeStructure)
        {
            Keys = keys ?? throw new ArgumentNullException(nameof(keys));
            ProgramIndices = programIndices ?? throw new ArgumentNullException(nameof(programIndices));
            FloorIndices = floorIndices ?? throw new ArgumentNullException(nameof(floorIndices));

            if (Keys.Count != ProgramIndices.Count || Keys.Count != FloorIndices.Count)
                throw new ArgumentException(
                    "Keys, ProgramIndices, and FloorIndices must have equal lengths.");

            IncludeCore = includeCore;
            StructureProgramIndex = structureProgramIndex;
            IncludeStructure = includeStructure;
        }
    }

    /// <summary>Result of extracting connected program footprints by floor.</summary>
    public sealed class FloorPlateExtractionResult
    {
        public IReadOnlyList<FloorPlateRegion> Regions { get; }
        public int SkippedUnallocatedVoxelCount { get; }
        public int SkippedCoreVoxelCount { get; }
        public int SkippedStructureVoxelCount { get; }

        public FloorPlateExtractionResult(
            IReadOnlyList<FloorPlateRegion> regions,
            int skippedUnallocatedVoxelCount,
            int skippedCoreVoxelCount,
            int skippedStructureVoxelCount)
        {
            Regions = regions;
            SkippedUnallocatedVoxelCount = skippedUnallocatedVoxelCount;
            SkippedCoreVoxelCount = skippedCoreVoxelCount;
            SkippedStructureVoxelCount = skippedStructureVoxelCount;
        }
    }

    /// <summary>
    /// Extracts deterministic connected XY footprints from an allocated voxel stack.
    /// The engine is Rhino-independent; world geometry is created by the Grasshopper adapter.
    /// </summary>
    public static class FloorPlateExtractionEngine
    {
        private const int Unallocated = -1;
        private const int Core = -2;

        private sealed class GroupData
        {
            public readonly Dictionary<FloorPlateCell, List<int>> CellSources =
                new Dictionary<FloorPlateCell, List<int>>();
        }

        public static FloorPlateExtractionResult Run(FloorPlateExtractionRequest request)
        {
            var groups = new Dictionary<Tuple<int, int>, GroupData>();
            int skippedUnallocated = 0;
            int skippedCore = 0;
            int skippedStructure = 0;

            for (int index = 0; index < request.Keys.Count; index++)
            {
                int assignment = request.ProgramIndices[index];

                if (assignment == Unallocated)
                {
                    skippedUnallocated++;
                    continue;
                }

                if (assignment == Core && !request.IncludeCore)
                {
                    skippedCore++;
                    continue;
                }

                if (assignment == request.StructureProgramIndex &&
                    request.StructureProgramIndex >= 0 &&
                    !request.IncludeStructure)
                {
                    skippedStructure++;
                    continue;
                }

                // Unknown negative sentinel values are not architectural program material.
                if (assignment < 0 && assignment != Core)
                    continue;

                var groupKey = Tuple.Create(assignment, request.FloorIndices[index]);
                if (!groups.TryGetValue(groupKey, out GroupData group))
                {
                    group = new GroupData();
                    groups.Add(groupKey, group);
                }

                var key = request.Keys[index];
                var cell = new FloorPlateCell(key.X, key.Y);

                if (!group.CellSources.TryGetValue(cell, out List<int> sources))
                {
                    sources = new List<int>();
                    group.CellSources.Add(cell, sources);
                }

                sources.Add(index);
            }

            var regions = new List<FloorPlateRegion>();

            foreach (var groupPair in groups
                .OrderBy(pair => pair.Key.Item1)
                .ThenBy(pair => pair.Key.Item2))
            {
                int programIndex = groupPair.Key.Item1;
                int floorIndex = groupPair.Key.Item2;
                var cells = new HashSet<FloorPlateCell>(groupPair.Value.CellSources.Keys);

                while (cells.Count > 0)
                {
                    FloorPlateCell seed = cells.OrderBy(cell => cell).First();
                    var queue = new Queue<FloorPlateCell>();
                    var footprint = new List<FloorPlateCell>();
                    var sourceIndices = new List<int>();

                    queue.Enqueue(seed);
                    cells.Remove(seed);

                    while (queue.Count > 0)
                    {
                        FloorPlateCell current = queue.Dequeue();
                        footprint.Add(current);
                        sourceIndices.AddRange(groupPair.Value.CellSources[current]);

                        foreach (FloorPlateCell neighbour in FourNeighbours(current))
                        {
                            if (cells.Remove(neighbour))
                                queue.Enqueue(neighbour);
                        }
                    }

                    footprint.Sort();
                    sourceIndices.Sort();

                    int minimumZ = sourceIndices
                        .Select(index => request.Keys[index].Z)
                        .Min();

                    regions.Add(new FloorPlateRegion(
                        programIndex,
                        floorIndex,
                        minimumZ,
                        footprint,
                        sourceIndices));
                }
            }

            var orderedRegions = regions
                .OrderBy(region => region.ProgramIndex)
                .ThenBy(region => region.FloorIndex)
                .ThenBy(region => region.FootprintCells[0])
                .ToList();

            return new FloorPlateExtractionResult(
                orderedRegions,
                skippedUnallocated,
                skippedCore,
                skippedStructure);
        }

        private static IEnumerable<FloorPlateCell> FourNeighbours(FloorPlateCell cell)
        {
            yield return new FloorPlateCell(cell.X - 1, cell.Y);
            yield return new FloorPlateCell(cell.X + 1, cell.Y);
            yield return new FloorPlateCell(cell.X, cell.Y - 1);
            yield return new FloorPlateCell(cell.X, cell.Y + 1);
        }
    }
}

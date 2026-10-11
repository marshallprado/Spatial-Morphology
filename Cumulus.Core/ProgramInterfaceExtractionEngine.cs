// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;

namespace Cumulus
{
    /// <summary>Integer voxel coordinate used by the program-interface extractor.</summary>
    public struct ProgramInterfaceVoxelKey : IEquatable<ProgramInterfaceVoxelKey>, IComparable<ProgramInterfaceVoxelKey>
    {
        public int X { get; private set; }
        public int Y { get; private set; }
        public int Z { get; private set; }

        public ProgramInterfaceVoxelKey(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public bool Equals(ProgramInterfaceVoxelKey other)
        {
            return X == other.X && Y == other.Y && Z == other.Z;
        }

        public override bool Equals(object obj)
        {
            return obj is ProgramInterfaceVoxelKey && Equals((ProgramInterfaceVoxelKey)obj);
        }

        public override int GetHashCode()
        {
            unchecked { return ((X * 397) ^ Y) * 397 ^ Z; }
        }

        public int CompareTo(ProgramInterfaceVoxelKey other)
        {
            int x = X.CompareTo(other.X);
            if (x != 0) return x;
            int y = Y.CompareTo(other.Y);
            return y != 0 ? y : Z.CompareTo(other.Z);
        }
    }

    /// <summary>Classifies an exact face in the canonical program-interface complex.</summary>
    public enum ProgramInterfaceFaceKind
    {
        ProgramToProgram,
        ProgramToCore,
        HorizontalOpenBoundaryCap
    }

    /// <summary>One exact shared voxel face or horizontal open-boundary cap.</summary>
    public sealed class ProgramInterfaceFace
    {
        public int ProgramA { get; private set; }
        public int ProgramB { get; private set; }
        public int VoxelA { get; private set; }
        public int VoxelB { get; private set; }
        public ProgramInterfaceVoxelKey KeyA { get; private set; }
        public int Axis { get; private set; }
        public int Direction { get; private set; }
        public ProgramInterfaceFaceKind Kind { get; private set; }
        public bool IsExteriorCap { get; private set; }

        public ProgramInterfaceFace(
            int programA,
            int programB,
            int voxelA,
            int voxelB,
            ProgramInterfaceVoxelKey keyA,
            int axis,
            int direction,
            ProgramInterfaceFaceKind kind,
            bool isExteriorCap)
        {
            ProgramA = programA;
            ProgramB = programB;
            VoxelA = voxelA;
            VoxelB = voxelB;
            KeyA = keyA;
            Axis = axis;
            Direction = direction;
            Kind = kind;
            IsExteriorCap = isExteriorCap;
        }
    }

    /// <summary>One aggregated direct program/core adjacency. Horizontal caps are excluded.</summary>
    public sealed class ProgramInterfaceAdjacency
    {
        public int ProgramA { get; private set; }
        public int ProgramB { get; private set; }
        public int FaceCount { get; private set; }

        public ProgramInterfaceAdjacency(int programA, int programB, int faceCount)
        {
            ProgramA = programA;
            ProgramB = programB;
            FaceCount = faceCount;
        }
    }

    /// <summary>Input for exact program-interface extraction.</summary>
    public sealed class ProgramInterfaceExtractionRequest
    {
        public IReadOnlyList<ProgramInterfaceVoxelKey> Keys { get; private set; }
        public IReadOnlyList<int> ProgramIndices { get; private set; }
        public bool IncludeCore { get; private set; }
        public bool CapHorizontalOpenFaces { get; private set; }

        public ProgramInterfaceExtractionRequest(
            IReadOnlyList<ProgramInterfaceVoxelKey> keys,
            IReadOnlyList<int> programIndices,
            bool includeCore,
            bool capHorizontalOpenFaces = false)
        {
            if (keys == null) throw new ArgumentNullException(nameof(keys));
            if (programIndices == null) throw new ArgumentNullException(nameof(programIndices));
            if (keys.Count != programIndices.Count)
                throw new ArgumentException("Keys and ProgramIndices must have equal lengths.");

            Keys = keys;
            ProgramIndices = programIndices;
            IncludeCore = includeCore;
            CapHorizontalOpenFaces = capHorizontalOpenFaces;
        }
    }

    /// <summary>Exact voxel-face interfaces, horizontal caps, and aggregated program adjacencies.</summary>
    public sealed class ProgramInterfaceExtractionResult
    {
        public IReadOnlyList<ProgramInterfaceFace> Faces { get; private set; }
        public IReadOnlyList<ProgramInterfaceAdjacency> Adjacencies { get; private set; }
        public int IgnoredUnallocatedContacts { get; private set; }
        public int IgnoredExteriorContacts { get; private set; }
        public int IgnoredSameProgramContacts { get; private set; }
        public int IgnoredCoreContacts { get; private set; }
        public int HorizontalUnallocatedCapCount { get; private set; }
        public int HorizontalExteriorCapCount { get; private set; }
        public int HorizontalExcludedCoreCapCount { get; private set; }

        public ProgramInterfaceExtractionResult(
            IReadOnlyList<ProgramInterfaceFace> faces,
            IReadOnlyList<ProgramInterfaceAdjacency> adjacencies,
            int ignoredUnallocatedContacts,
            int ignoredExteriorContacts,
            int ignoredSameProgramContacts,
            int ignoredCoreContacts,
            int horizontalUnallocatedCapCount,
            int horizontalExteriorCapCount,
            int horizontalExcludedCoreCapCount)
        {
            Faces = faces;
            Adjacencies = adjacencies;
            IgnoredUnallocatedContacts = ignoredUnallocatedContacts;
            IgnoredExteriorContacts = ignoredExteriorContacts;
            IgnoredSameProgramContacts = ignoredSameProgramContacts;
            IgnoredCoreContacts = ignoredCoreContacts;
            HorizontalUnallocatedCapCount = horizontalUnallocatedCapCount;
            HorizontalExteriorCapCount = horizontalExteriorCapCount;
            HorizontalExcludedCoreCapCount = horizontalExcludedCoreCapCount;
        }
    }

    /// <summary>
    /// Extracts canonical shared faces between allocated programs and optional
    /// core voxels. It leaves open side faces toward unallocated/exterior volume,
    /// while optionally retaining top and bottom faces as horizontal caps.
    /// </summary>
    public static class ProgramInterfaceExtractionEngine
    {
        private const int Unallocated = -1;
        private const int Core = -2;

        private static readonly int[,] PositiveAxisDeltas =
        {
            { 1, 0, 0 },
            { 0, 1, 0 },
            { 0, 0, 1 }
        };

        public static ProgramInterfaceExtractionResult Run(ProgramInterfaceExtractionRequest request)
        {
            var keyToIndex = new Dictionary<ProgramInterfaceVoxelKey, int>();

            for (int i = 0; i < request.Keys.Count; i++)
            {
                if (!keyToIndex.ContainsKey(request.Keys[i]))
                    keyToIndex.Add(request.Keys[i], i);
            }

            var faces = new List<ProgramInterfaceFace>();
            int unallocated = 0;
            int exterior = 0;
            int sameProgram = 0;
            int coreIgnored = 0;
            int unallocatedCaps = 0;
            int exteriorCaps = 0;
            int excludedCoreCaps = 0;

            // Positive axes visit a shared allocated interface exactly once.
            for (int index = 0; index < request.Keys.Count; index++)
            {
                int firstAssignment = request.ProgramIndices[index];

                if (!IsRegularProgramOrEligibleCore(firstAssignment, request.IncludeCore))
                    continue;

                ProgramInterfaceVoxelKey firstKey = request.Keys[index];

                for (int axis = 0; axis < 3; axis++)
                {
                    ProgramInterfaceVoxelKey neighbourKey = Offset(
                        firstKey,
                        PositiveAxisDeltas[axis, 0],
                        PositiveAxisDeltas[axis, 1],
                        PositiveAxisDeltas[axis, 2]);

                    int neighbourIndex;
                    if (!keyToIndex.TryGetValue(neighbourKey, out neighbourIndex))
                    {
                        exterior++;
                        continue;
                    }

                    int secondAssignment = request.ProgramIndices[neighbourIndex];

                    if (secondAssignment == Unallocated)
                    {
                        unallocated++;
                        continue;
                    }

                    if (!IsRegularProgramOrEligibleCore(secondAssignment, request.IncludeCore))
                    {
                        coreIgnored++;
                        continue;
                    }

                    if (firstAssignment == secondAssignment ||
                        (firstAssignment == Core && secondAssignment == Core))
                    {
                        sameProgram++;
                        continue;
                    }

                    int programA = firstAssignment;
                    int programB = secondAssignment;
                    int voxelA = index;
                    int voxelB = neighbourIndex;
                    ProgramInterfaceVoxelKey keyA = firstKey;
                    int direction = 1;

                    // Canonicalize regular-program pairs by index. Core remains
                    // ProgramB, so normals point regular program -> core.
                    if (programA == Core ||
                        (programB != Core && programA > programB))
                    {
                        int programSwap = programA;
                        programA = programB;
                        programB = programSwap;

                        int voxelSwap = voxelA;
                        voxelA = voxelB;
                        voxelB = voxelSwap;

                        keyA = neighbourKey;
                        direction = -1;
                    }

                    ProgramInterfaceFaceKind kind =
                        programB == Core
                            ? ProgramInterfaceFaceKind.ProgramToCore
                            : ProgramInterfaceFaceKind.ProgramToProgram;

                    faces.Add(new ProgramInterfaceFace(
                        programA,
                        programB,
                        voxelA,
                        voxelB,
                        keyA,
                        axis,
                        direction,
                        kind,
                        false));
                }
            }

            if (request.CapHorizontalOpenFaces)
            {
                // A cap is directional, so inspect both +/- local-Z neighbours
                // from every regular program voxel. Core voxels never emit caps.
                for (int index = 0; index < request.Keys.Count; index++)
                {
                    if (request.ProgramIndices[index] < 0)
                        continue;

                    ProgramInterfaceVoxelKey key = request.Keys[index];

                    for (int direction = -1; direction <= 1; direction += 2)
                    {
                        ProgramInterfaceVoxelKey neighbourKey =
                            Offset(key, 0, 0, direction);

                        int neighbourIndex;
                        bool hasNeighbour = keyToIndex.TryGetValue(
                            neighbourKey,
                            out neighbourIndex);

                        int neighbourAssignment = hasNeighbour
                            ? request.ProgramIndices[neighbourIndex]
                            : Unallocated;

                        // Allocated programs and included cores form real
                        // interfaces; same-program neighbours remain internal.
                        if (neighbourAssignment >= 0 ||
                            (request.IncludeCore && neighbourAssignment == Core))
                        {
                            continue;
                        }

                        bool isExterior = !hasNeighbour;
                        bool excludedCore =
                            hasNeighbour &&
                            neighbourAssignment == Core &&
                            !request.IncludeCore;

                        if (isExterior)
                            exteriorCaps++;
                        else if (excludedCore)
                            excludedCoreCaps++;
                        else
                            unallocatedCaps++;

                        faces.Add(new ProgramInterfaceFace(
                            request.ProgramIndices[index],
                            Unallocated,
                            index,
                            hasNeighbour ? neighbourIndex : -1,
                            key,
                            2,
                            direction,
                            ProgramInterfaceFaceKind.HorizontalOpenBoundaryCap,
                            isExterior));
                    }
                }
            }

            var adjacencies = faces
                .Where(face =>
                    face.Kind != ProgramInterfaceFaceKind.HorizontalOpenBoundaryCap)
                .GroupBy(face => Tuple.Create(face.ProgramA, face.ProgramB))
                .OrderBy(group => group.Key.Item1)
                .ThenBy(group => group.Key.Item2)
                .Select(group => new ProgramInterfaceAdjacency(
                    group.Key.Item1,
                    group.Key.Item2,
                    group.Count()))
                .ToList();

            return new ProgramInterfaceExtractionResult(
                faces,
                adjacencies,
                unallocated,
                exterior,
                sameProgram,
                coreIgnored,
                unallocatedCaps,
                exteriorCaps,
                excludedCoreCaps);
        }

        private static ProgramInterfaceVoxelKey Offset(
            ProgramInterfaceVoxelKey key,
            int x,
            int y,
            int z)
        {
            return new ProgramInterfaceVoxelKey(
                key.X + x,
                key.Y + y,
                key.Z + z);
        }

        private static bool IsRegularProgramOrEligibleCore(
            int assignment,
            bool includeCore)
        {
            return assignment >= 0 || (includeCore && assignment == Core);
        }
    }
}

// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;

namespace Cumulus
{
    /// <summary>Integer coordinate of one occupied voxel in a VoxelGrid.</summary>
    public struct VoxelBoxKey : IEquatable<VoxelBoxKey>, IComparable<VoxelBoxKey>
    {
        public int X { get; }
        public int Y { get; }
        public int Z { get; }

        public VoxelBoxKey(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public bool Equals(VoxelBoxKey other)
        {
            return X == other.X && Y == other.Y && Z == other.Z;
        }

        public override bool Equals(object obj)
        {
            return obj is VoxelBoxKey && Equals((VoxelBoxKey)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + X;
                hash = hash * 31 + Y;
                hash = hash * 31 + Z;
                return hash;
            }
        }

        public int CompareTo(VoxelBoxKey other)
        {
            int x = X.CompareTo(other.X);
            if (x != 0) return x;
            int y = Y.CompareTo(other.Y);
            return y != 0 ? y : Z.CompareTo(other.Z);
        }

        public override string ToString()
        {
            return string.Format("({0}, {1}, {2})", X, Y, Z);
        }
    }

    /// <summary>
    /// An axis-aligned grid-space box. Width, depth, and height are expressed
    /// in voxel counts and are always positive.
    /// </summary>
    public sealed class VoxelBoxRegion
    {
        public int ProgramIndex { get; }
        public VoxelBoxKey Minimum { get; }
        public int Width { get; }
        public int Depth { get; }
        public int Height { get; }
        public int VoxelCount { get; }

        public VoxelBoxRegion(
            int programIndex,
            VoxelBoxKey minimum,
            int width,
            int depth,
            int height)
        {
            if (width < 1) throw new ArgumentOutOfRangeException(nameof(width));
            if (depth < 1) throw new ArgumentOutOfRangeException(nameof(depth));
            if (height < 1) throw new ArgumentOutOfRangeException(nameof(height));

            ProgramIndex = programIndex;
            Minimum = minimum;
            Width = width;
            Depth = depth;
            Height = height;
            VoxelCount = checked(width * depth * height);
        }
    }

    /// <summary>
    /// Deterministically partitions assigned voxel cells into axis-aligned
    /// rectangular regions. Each iteration takes the globally largest available
    /// all-filled region for one program; ties use the lowest X, then Y, then Z
    /// origin followed by smaller dimensions, making repeated solves stable.
    /// </summary>
    public static class VoxelBoxMergeEngine
    {
        /// <summary>
        /// Merges all cells having a non-negative program index or the core
        /// sentinel (-2). Unassigned cells (-1) are intentionally excluded.
        /// </summary>
        public static IReadOnlyList<VoxelBoxRegion> Merge(
            IReadOnlyList<VoxelBoxKey> keys,
            IReadOnlyList<int> programIndices)
        {
            if (keys == null) throw new ArgumentNullException(nameof(keys));
            if (programIndices == null) throw new ArgumentNullException(nameof(programIndices));
            if (keys.Count != programIndices.Count)
                throw new ArgumentException(
                    "keys and programIndices must contain the same number of items.");

            var grouped = new Dictionary<int, HashSet<VoxelBoxKey>>();

            for (int i = 0; i < keys.Count; i++)
            {
                int program = programIndices[i];
                if (program == -1) continue;
                if (program < -2)
                    throw new ArgumentException(
                        "Program indices must be non-negative, -1 (unassigned), or -2 (core).",
                        nameof(programIndices));

                HashSet<VoxelBoxKey> cells;
                if (!grouped.TryGetValue(program, out cells))
                {
                    cells = new HashSet<VoxelBoxKey>();
                    grouped.Add(program, cells);
                }

                if (!cells.Add(keys[i]))
                    throw new ArgumentException(
                        "keys contains duplicate voxel coordinates.", nameof(keys));
            }

            var result = new List<VoxelBoxRegion>();
            foreach (int program in grouped.Keys.OrderBy(i => i))
                MergeProgram(program, grouped[program], result);

            return result;
        }

        private static void MergeProgram(
            int programIndex,
            HashSet<VoxelBoxKey> remaining,
            List<VoxelBoxRegion> result)
        {
            while (remaining.Count > 0)
            {
                VoxelBoxRegion best = null;

                // A valid region's minimum coordinate must be one of its cells.
                // Sorting makes both the search and every tie deterministic.
                foreach (VoxelBoxKey origin in remaining.OrderBy(k => k))
                {
                    VoxelBoxRegion candidate = FindLargestAt(origin, programIndex, remaining);
                    if (IsBetter(candidate, best))
                        best = candidate;
                }

                result.Add(best);
                Remove(best, remaining);
            }
        }

        private static VoxelBoxRegion FindLargestAt(
            VoxelBoxKey origin,
            int programIndex,
            HashSet<VoxelBoxKey> cells)
        {
            int maxWidth = 0;
            while (cells.Contains(new VoxelBoxKey(origin.X + maxWidth, origin.Y, origin.Z)))
                maxWidth++;

            VoxelBoxRegion best = new VoxelBoxRegion(programIndex, origin, 1, 1, 1);

            for (int width = 1; width <= maxWidth; width++)
            {
                int maxDepth = MaximumDepth(origin, width, cells);

                for (int depth = 1; depth <= maxDepth; depth++)
                {
                    int height = MaximumHeight(origin, width, depth, cells);
                    var candidate = new VoxelBoxRegion(
                        programIndex, origin, width, depth, height);

                    if (IsBetter(candidate, best))
                        best = candidate;
                }
            }

            return best;
        }

        private static int MaximumDepth(
            VoxelBoxKey origin,
            int width,
            HashSet<VoxelBoxKey> cells)
        {
            int depth = 0;

            while (true)
            {
                int y = origin.Y + depth;
                for (int x = 0; x < width; x++)
                    if (!cells.Contains(new VoxelBoxKey(origin.X + x, y, origin.Z)))
                        return depth;
                depth++;
            }
        }

        private static int MaximumHeight(
            VoxelBoxKey origin,
            int width,
            int depth,
            HashSet<VoxelBoxKey> cells)
        {
            int height = 0;

            while (true)
            {
                int z = origin.Z + height;
                for (int x = 0; x < width; x++)
                    for (int y = 0; y < depth; y++)
                        if (!cells.Contains(new VoxelBoxKey(origin.X + x, origin.Y + y, z)))
                            return height;
                height++;
            }
        }

        private static bool IsBetter(VoxelBoxRegion candidate, VoxelBoxRegion current)
        {
            if (current == null) return true;
            if (candidate.VoxelCount != current.VoxelCount)
                return candidate.VoxelCount > current.VoxelCount;

            int origin = candidate.Minimum.CompareTo(current.Minimum);
            if (origin != 0) return origin < 0;

            if (candidate.Width != current.Width) return candidate.Width < current.Width;
            if (candidate.Depth != current.Depth) return candidate.Depth < current.Depth;
            return candidate.Height < current.Height;
        }

        private static void Remove(
            VoxelBoxRegion box,
            HashSet<VoxelBoxKey> cells)
        {
            for (int x = 0; x < box.Width; x++)
                for (int y = 0; y < box.Depth; y++)
                    for (int z = 0; z < box.Height; z++)
                        cells.Remove(new VoxelBoxKey(
                            box.Minimum.X + x,
                            box.Minimum.Y + y,
                            box.Minimum.Z + z));
        }
    }
}

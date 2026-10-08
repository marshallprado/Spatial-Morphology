// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;

namespace Cumulus
{
    /// <summary>Integer grid coordinate used by the structural connectivity solver.</summary>
    public struct StructureVoxelKey : IEquatable<StructureVoxelKey>, IComparable<StructureVoxelKey>
    {
        public readonly int X;
        public readonly int Y;
        public readonly int Z;

        public StructureVoxelKey(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public bool Equals(StructureVoxelKey other) =>
            X == other.X && Y == other.Y && Z == other.Z;

        public override bool Equals(object obj) =>
            obj is StructureVoxelKey && Equals((StructureVoxelKey)obj);

        public override int GetHashCode()
        {
            unchecked { return ((X * 397) ^ Y) * 397 ^ Z; }
        }

        public int CompareTo(StructureVoxelKey other)
        {
            int compare = X.CompareTo(other.X);
            if (compare != 0) return compare;
            compare = Y.CompareTo(other.Y);
            return compare != 0 ? compare : Z.CompareTo(other.Z);
        }
    }

    /// <summary>One face-connected cluster of voxels assigned to a program.</summary>
    public sealed class ProgramVoxelCluster
    {
        public int ProgramIndex { get; private set; }
        public IReadOnlyList<int> VoxelIndices { get; private set; }

        public ProgramVoxelCluster(int programIndex, IReadOnlyList<int> voxelIndices)
        {
            ProgramIndex = programIndex;
            VoxelIndices = voxelIndices;
        }
    }

    /// <summary>Input for the architectural structural-connectivity optimizer.</summary>
    public sealed class StructureOptimizationRequest
    {
        public IReadOnlyList<StructureVoxelKey> Keys { get; private set; }
        public IReadOnlyList<int> ProgramIndices { get; private set; }
        public IReadOnlyCollection<int> GroundContactIndices { get; private set; }
        public double RetainedFraction { get; private set; }
        public int MaximumIterations { get; private set; }

        public StructureOptimizationRequest(
            IReadOnlyList<StructureVoxelKey> keys,
            IReadOnlyList<int> programIndices,
            IReadOnlyCollection<int> groundContactIndices,
            double retainedFraction,
            int maximumIterations)
        {
            if (keys == null) throw new ArgumentNullException(nameof(keys));
            if (programIndices == null) throw new ArgumentNullException(nameof(programIndices));
            if (groundContactIndices == null) throw new ArgumentNullException(nameof(groundContactIndices));
            if (keys.Count != programIndices.Count)
                throw new ArgumentException("Keys and ProgramIndices must have equal lengths.");

            Keys = keys;
            ProgramIndices = programIndices;
            GroundContactIndices = groundContactIndices;
            RetainedFraction = Math.Max(0.0, Math.Min(1.0, retainedFraction));
            MaximumIterations = Math.Max(1, maximumIterations);
        }
    }

    /// <summary>Result of an architectural, topology-like support routing pass.</summary>
    public sealed class StructureOptimizationResult
    {
        public IReadOnlyList<int> StructureIndices { get; private set; }
        public IReadOnlyList<double> CandidateImportance { get; private set; }
        public IReadOnlyList<ProgramVoxelCluster> ProgramClusters { get; private set; }
        public IReadOnlyList<IReadOnlyList<int>> ClusterPaths { get; private set; }
        public IReadOnlyList<IReadOnlyList<int>> UnsupportedClusters { get; private set; }
        public IReadOnlyList<int> UnsupportedIndices { get; private set; }
        public IReadOnlyList<Tuple<int, int>> NetworkEdges { get; private set; }
        public int CandidateCount { get; private set; }
        public int TargetStructureCount { get; private set; }
        public int GroundContactCount { get; private set; }

        public StructureOptimizationResult(
            IReadOnlyList<int> structureIndices,
            IReadOnlyList<double> candidateImportance,
            IReadOnlyList<ProgramVoxelCluster> programClusters,
            IReadOnlyList<IReadOnlyList<int>> clusterPaths,
            IReadOnlyList<IReadOnlyList<int>> unsupportedClusters,
            IReadOnlyList<int> unsupportedIndices,
            IReadOnlyList<Tuple<int, int>> networkEdges,
            int candidateCount,
            int targetStructureCount,
            int groundContactCount)
        {
            StructureIndices = structureIndices;
            CandidateImportance = candidateImportance;
            ProgramClusters = programClusters;
            ClusterPaths = clusterPaths;
            UnsupportedClusters = unsupportedClusters;
            UnsupportedIndices = unsupportedIndices;
            NetworkEdges = networkEdges;
            CandidateCount = candidateCount;
            TargetStructureCount = targetStructureCount;
            GroundContactCount = groundContactCount;
        }
    }

    /// <summary>
    /// Architectural load-path solver. It is not a finite-element or code-compliance
    /// calculation. All program and core voxels are transmissive; only unallocated
    /// voxels can be retained as generated structure.
    /// </summary>
    public static class StructureOptimizationEngine
    {
        private const int Unallocated = -1;

        private sealed class QueueEntry
        {
            public int Index;
            public double Cost;
            public QueueEntry(int index, double cost) { Index = index; Cost = cost; }
        }

        public static StructureOptimizationResult Run(StructureOptimizationRequest request)
        {
            var keys = request.Keys;
            var assignments = request.ProgramIndices;
            int count = keys.Count;

            var keyToIndex = new Dictionary<StructureVoxelKey, int>();
            for (int index = 0; index < count; index++)
                keyToIndex[keys[index]] = index;

            var neighbours = BuildNeighbours(keys, keyToIndex);
            var candidates = Enumerable.Range(0, count)
                .Where(index => assignments[index] == Unallocated)
                .ToList();

            var ground = new HashSet<int>(
                request.GroundContactIndices.Where(index => index >= 0 && index < count));

            var clusters = FindProgramClusters(assignments, neighbours);

            // Process large clusters first. Each pass retains only the
            // candidates used by the current load paths. Candidates retained by
            // the preceding pass are cheaper, so repeated passes consolidate
            // routes into shared trunks and discard redundant detours.
            var orderedClusters = clusters
                .OrderByDescending(cluster => cluster.VoxelIndices.Count)
                .ThenBy(cluster => cluster.ProgramIndex)
                .ThenBy(cluster => cluster.VoxelIndices.Min())
                .ToList();

            var selected = new HashSet<int>();
            var importance = new double[count];
            var routedPaths = new Dictionary<int, IReadOnlyList<int>>();
            var edges = new HashSet<Tuple<int, int>>(new EdgeComparer());

            int passLimit = Math.Max(1, request.MaximumIterations);
            for (int pass = 0; pass < passLimit; pass++)
            {
                var nextSelected = new HashSet<int>();
                var preferred = new HashSet<int>(selected);
                var nextImportance = new double[count];
                var nextPaths = new Dictionary<int, IReadOnlyList<int>>();
                var nextEdges = new HashSet<Tuple<int, int>>(new EdgeComparer());

                foreach (var cluster in orderedClusters)
                {
                    var path = FindPathToGround(
                        cluster.VoxelIndices, ground, assignments, neighbours, keys, preferred);

                    nextPaths[cluster.VoxelIndices.Min()] = path;
                    if (path.Count == 0)
                        continue;

                    for (int i = 0; i < path.Count; i++)
                    {
                        int voxel = path[i];
                        if (assignments[voxel] == Unallocated)
                        {
                            nextImportance[voxel] += cluster.VoxelIndices.Count;
                            nextSelected.Add(voxel);
                            preferred.Add(voxel);
                        }

                        if (i > 0)
                        {
                            int a = Math.Min(path[i - 1], voxel);
                            int b = Math.Max(path[i - 1], voxel);
                            nextEdges.Add(Tuple.Create(a, b));
                        }
                    }
                }

                bool converged = nextSelected.SetEquals(selected);
                selected = nextSelected;
                importance = nextImportance;
                routedPaths = nextPaths;
                edges = nextEdges;

                if (converged)
                    break;
            }

            // The requested fraction is a maximum material budget, not a reason
            // to add structurally unnecessary voxels. If required paths exceed the
            // target, keep them and report the overrun rather than disconnecting
            // occupied volume.
            int target = (int)Math.Ceiling(candidates.Count * request.RetainedFraction);

            // Return path branches in stable program/cluster order. Paths were
            // deliberately routed in descending cluster-size order so later
            // branches can reuse earlier structural trunks.
            var pathsByCluster = new List<IReadOnlyList<int>>();
            foreach (var cluster in clusters)
                pathsByCluster.Add(routedPaths[cluster.VoxelIndices.Min()]);

            var grounded = FloodGrounded(ground, assignments, selected, neighbours);
            var unsupported = new List<IReadOnlyList<int>>();
            for (int i = 0; i < clusters.Count; i++)
            {
                var cluster = clusters[i];
                if (!cluster.VoxelIndices.Any(grounded.Contains))
                    unsupported.Add(cluster.VoxelIndices);
            }

            return new StructureOptimizationResult(
                selected.OrderBy(index => index).ToList(),
                importance,
                clusters,
                pathsByCluster,
                unsupported,
                Enumerable.Range(0, count)
                    .Where(index => assignments[index] != Unallocated && !grounded.Contains(index))
                    .OrderBy(index => index)
                    .ToList(),
                edges.OrderBy(edge => edge.Item1).ThenBy(edge => edge.Item2).ToList(),
                candidates.Count,
                target,
                ground.Count);
        }

        private static List<List<int>> BuildNeighbours(
            IReadOnlyList<StructureVoxelKey> keys,
            Dictionary<StructureVoxelKey, int> keyToIndex)
        {
            var result = new List<List<int>>(keys.Count);
            int[] delta = { 1, -1 };

            for (int i = 0; i < keys.Count; i++)
            {
                var key = keys[i];
                var list = new List<int>(6);

                foreach (int d in delta)
                {
                    AddNeighbour(new StructureVoxelKey(key.X + d, key.Y, key.Z), keyToIndex, list);
                    AddNeighbour(new StructureVoxelKey(key.X, key.Y + d, key.Z), keyToIndex, list);
                    AddNeighbour(new StructureVoxelKey(key.X, key.Y, key.Z + d), keyToIndex, list);
                }

                list.Sort();
                result.Add(list);
            }
            return result;
        }

        private static void AddNeighbour(
            StructureVoxelKey key, Dictionary<StructureVoxelKey, int> map, List<int> list)
        {
            int index;
            if (map.TryGetValue(key, out index))
                list.Add(index);
        }

        private static List<ProgramVoxelCluster> FindProgramClusters(
            IReadOnlyList<int> assignments, IReadOnlyList<List<int>> neighbours)
        {
            var visited = new bool[assignments.Count];
            var clusters = new List<ProgramVoxelCluster>();

            for (int start = 0; start < assignments.Count; start++)
            {
                int program = assignments[start];
                if (program < 0 || visited[start])
                    continue;

                var queue = new Queue<int>();
                var voxels = new List<int>();
                queue.Enqueue(start);
                visited[start] = true;

                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    voxels.Add(current);

                    foreach (int next in neighbours[current])
                    {
                        if (!visited[next] && assignments[next] == program)
                        {
                            visited[next] = true;
                            queue.Enqueue(next);
                        }
                    }
                }

                voxels.Sort();
                clusters.Add(new ProgramVoxelCluster(program, voxels));
            }

            return clusters
                .OrderBy(cluster => cluster.ProgramIndex)
                .ThenBy(cluster => cluster.VoxelIndices.Min())
                .ToList();
        }

        private static IReadOnlyList<int> FindPathToGround(
            IReadOnlyList<int> sources,
            HashSet<int> ground,
            IReadOnlyList<int> assignments,
            IReadOnlyList<List<int>> neighbours,
            IReadOnlyList<StructureVoxelKey> keys,
            HashSet<int> selected)
        {
            if (ground.Count == 0)
                return new List<int>();

            var distances = new double[assignments.Count];
            var previous = new int[assignments.Count];
            var visited = new bool[assignments.Count];
            for (int i = 0; i < distances.Length; i++)
            {
                distances[i] = double.PositiveInfinity;
                previous[i] = -1;
            }

            var queue = new List<QueueEntry>();
            foreach (int source in sources.OrderBy(index => index))
            {
                distances[source] = 0.0;
                queue.Add(new QueueEntry(source, 0.0));
            }

            int destination = -1;
            while (queue.Count > 0)
            {
                int bestPosition = 0;
                for (int i = 1; i < queue.Count; i++)
                {
                    if (queue[i].Cost < queue[bestPosition].Cost ||
                        (Math.Abs(queue[i].Cost - queue[bestPosition].Cost) < 1e-9 &&
                         queue[i].Index < queue[bestPosition].Index))
                        bestPosition = i;
                }

                var entry = queue[bestPosition];
                queue.RemoveAt(bestPosition);
                int current = entry.Index;

                if (visited[current])
                    continue;
                visited[current] = true;

                if (ground.Contains(current))
                {
                    destination = current;
                    break;
                }

                foreach (int next in neighbours[current])
                {
                    if (visited[next])
                        continue;

                    double nextCost = distances[current] +
                        StepCost(current, next, assignments, keys, selected);

                    if (nextCost + 1e-9 < distances[next] ||
                        (Math.Abs(nextCost - distances[next]) < 1e-9 &&
                         current < previous[next]))
                    {
                        distances[next] = nextCost;
                        previous[next] = current;
                        queue.Add(new QueueEntry(next, nextCost));
                    }
                }
            }

            if (destination < 0)
                return new List<int>();

            var path = new List<int>();
            for (int current = destination; current >= 0; current = previous[current])
                path.Add(current);

            path.Reverse();
            return path;
        }

        private static double StepCost(
            int current, int next, IReadOnlyList<int> assignments,
            IReadOnlyList<StructureVoxelKey> keys, HashSet<int> selected)
        {
            // Direction is evaluated from load source toward ground. Downward is
            // preferred, lateral transfer is allowed, and upward detours cost more.
            int dz = keys[next].Z - keys[current].Z;
            double movement = dz < 0 ? 1.0 : dz == 0 ? 4.0 : 8.0;

            if (assignments[next] != Unallocated)
                return movement * 0.10;

            if (selected.Contains(next))
                return movement * 0.20;

            return movement;
        }

        private static HashSet<int> FloodGrounded(
            HashSet<int> ground,
            IReadOnlyList<int> assignments,
            HashSet<int> selected,
            IReadOnlyList<List<int>> neighbours)
        {
            var grounded = new HashSet<int>();
            var queue = new Queue<int>();

            foreach (int index in ground)
            {
                grounded.Add(index);
                queue.Enqueue(index);
            }

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (int next in neighbours[current])
                {
                    bool solid = assignments[next] != Unallocated || selected.Contains(next);
                    if (solid && grounded.Add(next))
                        queue.Enqueue(next);
                }
            }

            return grounded;
        }

        private sealed class EdgeComparer : IEqualityComparer<Tuple<int, int>>
        {
            public bool Equals(Tuple<int, int> x, Tuple<int, int> y) =>
                x != null && y != null && x.Item1 == y.Item1 && x.Item2 == y.Item2;

            public int GetHashCode(Tuple<int, int> obj)
            {
                unchecked { return obj.Item1 * 397 ^ obj.Item2; }
            }
        }
    }
}

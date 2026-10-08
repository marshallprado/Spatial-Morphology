// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;

namespace Cumulus
{
    public struct StructureVoxelKey : IEquatable<StructureVoxelKey>, IComparable<StructureVoxelKey>
    {
        public readonly int X;
        public readonly int Y;
        public readonly int Z;

        public StructureVoxelKey(int x, int y, int z) { X = x; Y = y; Z = z; }
        public bool Equals(StructureVoxelKey other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object obj) => obj is StructureVoxelKey && Equals((StructureVoxelKey)obj);
        public override int GetHashCode() { unchecked { return ((X * 397) ^ Y) * 397 ^ Z; } }
        public int CompareTo(StructureVoxelKey other)
        {
            int c = X.CompareTo(other.X);
            if (c != 0) return c;
            c = Y.CompareTo(other.Y);
            return c != 0 ? c : Z.CompareTo(other.Z);
        }
    }

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

    public sealed class StructureOptimizationResult
    {
        public IReadOnlyList<int> StructureIndices { get; private set; }
        public IReadOnlyList<double> CandidateImportance { get; private set; }
        public IReadOnlyList<double> VoxelCapacity { get; private set; }
        public IReadOnlyList<ProgramVoxelCluster> ProgramClusters { get; private set; }
        public IReadOnlyList<IReadOnlyList<int>> ClusterPaths { get; private set; }
        public IReadOnlyList<IReadOnlyList<int>> UnsupportedClusters { get; private set; }
        public IReadOnlyList<int> UnsupportedIndices { get; private set; }
        public IReadOnlyList<int> WeakClusterIndices { get; private set; }
        public IReadOnlyList<Tuple<int, int>> NetworkEdges { get; private set; }
        public int CandidateCount { get; private set; }
        public int TargetStructureCount { get; private set; }
        public int GroundContactCount { get; private set; }

        public StructureOptimizationResult(
            IReadOnlyList<int> structureIndices,
            IReadOnlyList<double> candidateImportance,
            IReadOnlyList<double> voxelCapacity,
            IReadOnlyList<ProgramVoxelCluster> programClusters,
            IReadOnlyList<IReadOnlyList<int>> clusterPaths,
            IReadOnlyList<IReadOnlyList<int>> unsupportedClusters,
            IReadOnlyList<int> unsupportedIndices,
            IReadOnlyList<int> weakClusterIndices,
            IReadOnlyList<Tuple<int, int>> networkEdges,
            int candidateCount,
            int targetStructureCount,
            int groundContactCount)
        {
            StructureIndices = structureIndices;
            CandidateImportance = candidateImportance;
            VoxelCapacity = voxelCapacity;
            ProgramClusters = programClusters;
            ClusterPaths = clusterPaths;
            UnsupportedClusters = unsupportedClusters;
            UnsupportedIndices = unsupportedIndices;
            WeakClusterIndices = weakClusterIndices;
            NetworkEdges = networkEdges;
            CandidateCount = candidateCount;
            TargetStructureCount = targetStructureCount;
            GroundContactCount = groundContactCount;
        }
    }

    /// <summary>
    /// Architectural load-path optimizer, not a finite-element calculation.
    /// Each allocated program voxel is routed independently. Downward paths keep
    /// full conceptual capacity, lateral paths reduce it, and upward paths reduce
    /// it severely. Floating core voxels are transmissive but are never roots.
    /// </summary>
    public static class StructureOptimizationEngine
    {
        private const int Unallocated = -1;
        private const double WeakCapacityThreshold = 0.25;

        private sealed class QueueEntry
        {
            public int Index;
            public double Cost;
            public QueueEntry(int index, double cost) { Index = index; Cost = cost; }
        }

        private sealed class PathRoute
        {
            public IReadOnlyList<int> Nodes;
            public double Cost;
            public double Capacity;
            public PathRoute(IReadOnlyList<int> nodes, double cost, double capacity)
            {
                Nodes = nodes;
                Cost = cost;
                Capacity = capacity;
            }
        }

        public static StructureOptimizationResult Run(StructureOptimizationRequest request)
        {
            var keys = request.Keys;
            var assignments = request.ProgramIndices;
            int count = keys.Count;

            var map = new Dictionary<StructureVoxelKey, int>();
            for (int i = 0; i < count; i++) map[keys[i]] = i;

            var neighbours = BuildNeighbours(keys, map);
            var candidates = Enumerable.Range(0, count)
                .Where(i => assignments[i] == Unallocated).ToList();
            var ground = new HashSet<int>(request.GroundContactIndices
                .Where(i => i >= 0 && i < count));

            var clusters = FindProgramClusters(assignments, neighbours);
            var clusterForSource = new Dictionary<int, int>();
            for (int c = 0; c < clusters.Count; c++)
                foreach (int voxel in clusters[c].VoxelIndices)
                    clusterForSource[voxel] = c;

            // Highest source voxels route first. This produces stable, gravity-led
            // trunks before lower voxels are allowed to join them.
            var sources = clusters.SelectMany(c => c.VoxelIndices)
                .OrderByDescending(i => keys[i].Z)
                .ThenBy(i => clusterForSource[i])
                .ThenBy(i => i)
                .ToList();

            var selected = new HashSet<int>();
            var importance = new double[count];
            var capacities = new double[count];
            var routes = new Dictionary<int, PathRoute>();
            var edges = new HashSet<Tuple<int, int>>(new EdgeComparer());

            for (int pass = 0; pass < request.MaximumIterations; pass++)
            {
                // Only an already-grounded network earns the shared-trunk discount.
                // A floating core or floating previously selected material remains
                // traversable, but it is not an attractive route destination.
                var reusableGrounded = FloodGrounded(ground, assignments, selected, neighbours);
                var nextSelected = new HashSet<int>();
                var nextImportance = new double[count];
                var nextCapacities = new double[count];
                var nextRoutes = new Dictionary<int, PathRoute>();
                var nextEdges = new HashSet<Tuple<int, int>>(new EdgeComparer());

                foreach (int source in sources)
                {
                    var route = FindPathToGround(
                        source, ground, reusableGrounded, assignments, neighbours, keys);

                    if (route == null)
                        continue;

                    nextRoutes[source] = route;
                    nextCapacities[source] = route.Capacity;

                    double runningCapacity = 1.0;
                    for (int i = 0; i < route.Nodes.Count; i++)
                    {
                        int voxel = route.Nodes[i];

                        if (i > 0)
                        {
                            runningCapacity *= StepCapacity(
                                route.Nodes[i - 1], voxel, keys);

                            int a = Math.Min(route.Nodes[i - 1], voxel);
                            int b = Math.Max(route.Nodes[i - 1], voxel);
                            nextEdges.Add(Tuple.Create(a, b));
                        }

                        if (assignments[voxel] == Unallocated)
                        {
                            nextSelected.Add(voxel);
                            // Each program voxel carries one conceptual unit.
                            // Weak lateral/upward routes contribute less trunk value.
                            nextImportance[voxel] += route.Capacity;
                        }
                        else if (assignments[voxel] < 0)
                        {
                            // Core capacity is the strongest conceptual load route
                            // that reaches that core voxel.
                            nextCapacities[voxel] = Math.Max(
                                nextCapacities[voxel], runningCapacity);
                        }
                    }
                }

                bool converged = nextSelected.SetEquals(selected);
                selected = nextSelected;
                importance = nextImportance;
                capacities = nextCapacities;
                routes = nextRoutes;
                edges = nextEdges;

                if (converged) break;
            }

            int target = (int)Math.Ceiling(candidates.Count * request.RetainedFraction);
            var grounded = FloodGrounded(ground, assignments, selected, neighbours);

            var clusterPaths = new List<IReadOnlyList<int>>();
            var unsupportedClusters = new List<IReadOnlyList<int>>();
            var weakClusters = new List<int>();

            for (int clusterIndex = 0; clusterIndex < clusters.Count; clusterIndex++)
            {
                var cluster = clusters[clusterIndex];
                var clusterRoutes = cluster.VoxelIndices
                    .Where(routes.ContainsKey)
                    .Select(i => new { Source = i, Route = routes[i] })
                    .ToList();

                // The diagnostic polyline is the cluster's strongest route. All
                // per-voxel routes still contribute to SN and importance.
                var representative = clusterRoutes
                    .OrderByDescending(x => x.Route.Capacity)
                    .ThenBy(x => x.Route.Cost)
                    .ThenBy(x => x.Source)
                    .FirstOrDefault();

                clusterPaths.Add(representative == null
                    ? (IReadOnlyList<int>)new List<int>()
                    : representative.Route.Nodes);

                bool isUnsupported = cluster.VoxelIndices.Any(i => !grounded.Contains(i));
                if (isUnsupported)
                    unsupportedClusters.Add(cluster.VoxelIndices);

                bool weak = cluster.VoxelIndices.Any(i =>
                    !routes.ContainsKey(i) || routes[i].Capacity <= WeakCapacityThreshold);

                if (weak) weakClusters.Add(clusterIndex);
            }

            var unsupportedIndices = Enumerable.Range(0, count)
                .Where(i => assignments[i] != Unallocated && !grounded.Contains(i))
                .OrderBy(i => i)
                .ToList();

            return new StructureOptimizationResult(
                selected.OrderBy(i => i).ToList(),
                importance,
                capacities,
                clusters,
                clusterPaths,
                unsupportedClusters,
                unsupportedIndices,
                weakClusters,
                edges.OrderBy(e => e.Item1).ThenBy(e => e.Item2).ToList(),
                candidates.Count,
                target,
                ground.Count);
        }

        private static List<List<int>> BuildNeighbours(
            IReadOnlyList<StructureVoxelKey> keys,
            Dictionary<StructureVoxelKey, int> map)
        {
            var result = new List<List<int>>(keys.Count);
            int[] delta = { -1, 1 };

            for (int i = 0; i < keys.Count; i++)
            {
                var key = keys[i];
                var list = new List<int>(6);
                foreach (int d in delta)
                {
                    AddNeighbour(new StructureVoxelKey(key.X + d, key.Y, key.Z), map, list);
                    AddNeighbour(new StructureVoxelKey(key.X, key.Y + d, key.Z), map, list);
                    AddNeighbour(new StructureVoxelKey(key.X, key.Y, key.Z + d), map, list);
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
            if (map.TryGetValue(key, out index)) list.Add(index);
        }

        private static List<ProgramVoxelCluster> FindProgramClusters(
            IReadOnlyList<int> assignments, IReadOnlyList<List<int>> neighbours)
        {
            var visited = new bool[assignments.Count];
            var clusters = new List<ProgramVoxelCluster>();

            for (int start = 0; start < assignments.Count; start++)
            {
                int program = assignments[start];
                if (program < 0 || visited[start]) continue;

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

            return clusters.OrderBy(c => c.ProgramIndex)
                .ThenBy(c => c.VoxelIndices.Min()).ToList();
        }

        private static PathRoute FindPathToGround(
            int source,
            HashSet<int> ground,
            HashSet<int> reusableGrounded,
            IReadOnlyList<int> assignments,
            IReadOnlyList<List<int>> neighbours,
            IReadOnlyList<StructureVoxelKey> keys)
        {
            if (ground.Count == 0) return null;

            var destinations = new HashSet<int>(ground);
            destinations.UnionWith(reusableGrounded);

            var distances = Enumerable.Repeat(double.PositiveInfinity, assignments.Count).ToArray();
            var previous = Enumerable.Repeat(-1, assignments.Count).ToArray();
            var visited = new bool[assignments.Count];
            var queue = new List<QueueEntry> { new QueueEntry(source, 0.0) };
            distances[source] = 0.0;
            int destination = -1;

            while (queue.Count > 0)
            {
                int best = 0;
                for (int i = 1; i < queue.Count; i++)
                {
                    if (queue[i].Cost < queue[best].Cost ||
                        (Math.Abs(queue[i].Cost - queue[best].Cost) < 1e-9 &&
                         queue[i].Index < queue[best].Index))
                        best = i;
                }

                var entry = queue[best];
                queue.RemoveAt(best);
                int current = entry.Index;
                if (visited[current]) continue;
                visited[current] = true;

                if (destinations.Contains(current))
                {
                    destination = current;
                    break;
                }

                foreach (int next in neighbours[current])
                {
                    if (visited[next]) continue;

                    double cost = distances[current] +
                        StepCost(current, next, keys, reusableGrounded);

                    if (cost + 1e-9 < distances[next] ||
                        (Math.Abs(cost - distances[next]) < 1e-9 &&
                         current < previous[next]))
                    {
                        distances[next] = cost;
                        previous[next] = current;
                        queue.Add(new QueueEntry(next, cost));
                    }
                }
            }

            if (destination < 0) return null;

            var nodes = new List<int>();
            for (int current = destination; current >= 0; current = previous[current])
                nodes.Add(current);
            nodes.Reverse();

            double capacity = 1.0;
            for (int i = 1; i < nodes.Count; i++)
                capacity *= StepCapacity(nodes[i - 1], nodes[i], keys);

            return new PathRoute(nodes, distances[destination], capacity);
        }

        private static double StepCost(
            int current, int next,
            IReadOnlyList<StructureVoxelKey> keys,
            HashSet<int> reusableGrounded)
        {
            int dz = keys[next].Z - keys[current].Z;
            double movement = dz < 0 ? 1.0 : dz == 0 ? 4.0 : 8.0;

            // Reusing only a network already proven connected to construction-plane
            // ground is cheap. A floating core remains transmissive but gets no
            // artificial shared-trunk incentive.
            return reusableGrounded.Contains(next) ? movement * 0.20 : movement;
        }

        private static double StepCapacity(
            int current, int next, IReadOnlyList<StructureVoxelKey> keys)
        {
            int dz = keys[next].Z - keys[current].Z;
            return dz < 0 ? 1.0 : dz == 0 ? 0.25 : 0.125;
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
                    if (solid && grounded.Add(next)) queue.Enqueue(next);
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

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
        public int MaximumCantileverVoxels { get; private set; }

        public StructureOptimizationRequest(
            IReadOnlyList<StructureVoxelKey> keys,
            IReadOnlyList<int> programIndices,
            IReadOnlyCollection<int> groundContactIndices,
            double retainedFraction,
            int maximumIterations)
            : this(keys, programIndices, groundContactIndices, retainedFraction, maximumIterations, 4)
        {
        }

        public StructureOptimizationRequest(
            IReadOnlyList<StructureVoxelKey> keys,
            IReadOnlyList<int> programIndices,
            IReadOnlyCollection<int> groundContactIndices,
            double retainedFraction,
            int maximumIterations,
            int maximumCantileverVoxels)
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
            MaximumCantileverVoxels = Math.Max(0, maximumCantileverVoxels);
        }
    }

    public sealed class StructureOptimizationResult
    {
        public IReadOnlyList<int> StructureIndices { get; private set; }
        public IReadOnlyList<double> CandidateImportance { get; private set; }
        public IReadOnlyList<double> VoxelCapacity { get; private set; }
        public IReadOnlyList<ProgramVoxelCluster> ProgramClusters { get; private set; }

        // Outer list: program clusters. Inner list: one complete source-to-ground path
        // per allocated program voxel in that cluster.
        public IReadOnlyList<IReadOnlyList<IReadOnlyList<int>>> ClusterPaths { get; private set; }

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
            IReadOnlyList<IReadOnlyList<IReadOnlyList<int>>> clusterPaths,
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
    /// Architectural load-path optimizer. It is not a finite-element calculation.
    /// All program and core cells are transmissive. A route can move only downward
    /// or horizontally; upward support is forbidden. Horizontal runs are limited by
    /// StructureOptimizationRequest.MaximumCantileverVoxels.
    /// </summary>
    public static class StructureOptimizationEngine
    {
        private const int Unallocated = -1;
        private const int Core = -2;
        private const double WeakCapacityThreshold = 0.25;

        private struct RouteState : IEquatable<RouteState>
        {
            public readonly int Index;
            public readonly int HorizontalRun;

            public RouteState(int index, int horizontalRun)
            {
                Index = index;
                HorizontalRun = horizontalRun;
            }

            public bool Equals(RouteState other) =>
                Index == other.Index && HorizontalRun == other.HorizontalRun;

            public override bool Equals(object obj) =>
                obj is RouteState && Equals((RouteState)obj);

            public override int GetHashCode()
            {
                unchecked { return Index * 397 ^ HorizontalRun; }
            }
        }

        private sealed class QueueEntry
        {
            public RouteState State;
            public double Cost;

            public QueueEntry(RouteState state, double cost)
            {
                State = state;
                Cost = cost;
            }
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
            for (int i = 0; i < count; i++)
                map[keys[i]] = i;

            var neighbours = BuildNeighbours(keys, map);
            var candidates = Enumerable.Range(0, count)
                .Where(i => assignments[i] == Unallocated)
                .ToList();
            var ground = new HashSet<int>(request.GroundContactIndices
                .Where(i => i >= 0 && i < count));

            var clusters = FindProgramClusters(assignments, neighbours);
            var coreSources = Enumerable.Range(0, count)
                .Where(i => assignments[i] == Core)
                .OrderByDescending(i => keys[i].Z)
                .ThenBy(i => i)
                .ToList();
            var programSources = clusters.SelectMany(cluster => cluster.VoxelIndices)
                .OrderByDescending(i => keys[i].Z)
                .ThenBy(i => i)
                .ToList();

            var selected = new HashSet<int>();
            var importance = new double[count];
            var capacity = new double[count];
            var coreRoutes = new Dictionary<int, PathRoute>();
            var programRoutes = new Dictionary<int, PathRoute>();
            var edges = new HashSet<Tuple<int, int>>(new EdgeComparer());

            for (int pass = 0; pass < request.MaximumIterations; pass++)
            {
                var nextSelected = new HashSet<int>();
                var nextImportance = new double[count];
                var nextCapacity = new double[count];
                var nextCoreRoutes = new Dictionary<int, PathRoute>();
                var nextProgramRoutes = new Dictionary<int, PathRoute>();
                var nextEdges = new HashSet<Tuple<int, int>>(new EdgeComparer());

                // Maps each proven grounded support cell to its directed continuation
                // to construction-plane ground. A floating core never enters this map.
                var groundedTails = new Dictionary<int, IReadOnlyList<int>>();

                // Establish the core-to-ground network first. Programs can then attach
                // to this nearest valid core network instead of growing long columns.
                foreach (int source in coreSources)
                {
                    var route = FindPath(
                        source, ground, groundedTails, keys, neighbours,
                        request.MaximumCantileverVoxels);

                    if (route == null)
                        continue;

                    var fullRoute = AppendGroundTail(route, groundedTails, keys);
                    nextCoreRoutes[source] = fullRoute;
                    AddRouteToGroundedTails(fullRoute.Nodes, groundedTails);
                    AddRouteToSelection(
                        fullRoute, assignments, nextSelected, nextImportance,
                        nextEdges, 1.0);
                    nextCapacity[source] = fullRoute.Capacity;
                }

                // Highest program cells route first, which makes the resulting
                // grounded network attractive to lower or adjacent program cells.
                foreach (int source in programSources)
                {
                    var route = FindPath(
                        source, ground, groundedTails, keys, neighbours,
                        request.MaximumCantileverVoxels);

                    if (route == null)
                        continue;

                    var fullRoute = AppendGroundTail(route, groundedTails, keys);
                    nextProgramRoutes[source] = fullRoute;
                    AddRouteToGroundedTails(fullRoute.Nodes, groundedTails);
                    AddRouteToSelection(
                        fullRoute, assignments, nextSelected, nextImportance,
                        nextEdges, fullRoute.Capacity);
                    nextCapacity[source] = fullRoute.Capacity;
                }

                bool converged = nextSelected.SetEquals(selected);
                selected = nextSelected;
                importance = nextImportance;
                capacity = nextCapacity;
                coreRoutes = nextCoreRoutes;
                programRoutes = nextProgramRoutes;
                edges = nextEdges;

                if (converged)
                    break;
            }

            int target = (int)Math.Ceiling(candidates.Count * request.RetainedFraction);

            var clusterPaths = new List<IReadOnlyList<IReadOnlyList<int>>>();
            var unsupportedClusters = new List<IReadOnlyList<int>>();
            var weakClusters = new List<int>();

            for (int clusterIndex = 0; clusterIndex < clusters.Count; clusterIndex++)
            {
                var cluster = clusters[clusterIndex];
                var paths = cluster.VoxelIndices
                    .Where(programRoutes.ContainsKey)
                    .OrderBy(i => i)
                    .Select(i => programRoutes[i].Nodes)
                    .ToList();

                clusterPaths.Add(paths);

                bool unsupported = cluster.VoxelIndices.Any(i => !programRoutes.ContainsKey(i));
                if (unsupported)
                    unsupportedClusters.Add(cluster.VoxelIndices);

                bool weak = cluster.VoxelIndices.Any(i =>
                    !programRoutes.ContainsKey(i) ||
                    programRoutes[i].Capacity <= WeakCapacityThreshold);

                if (weak)
                    weakClusters.Add(clusterIndex);
            }

            var unsupportedIndices = Enumerable.Range(0, count)
                .Where(i =>
                    (assignments[i] >= 0 && !programRoutes.ContainsKey(i)) ||
                    (assignments[i] == Core && !coreRoutes.ContainsKey(i)))
                .OrderBy(i => i)
                .ToList();

            return new StructureOptimizationResult(
                selected.OrderBy(i => i).ToList(),
                importance,
                capacity,
                clusters,
                clusterPaths,
                unsupportedClusters,
                unsupportedIndices,
                weakClusters,
                edges.OrderBy(edge => edge.Item1).ThenBy(edge => edge.Item2).ToList(),
                candidates.Count,
                target,
                ground.Count);
        }

        private static void AddRouteToSelection(
            PathRoute route,
            IReadOnlyList<int> assignments,
            HashSet<int> selected,
            double[] importance,
            HashSet<Tuple<int, int>> edges,
            double contribution)
        {
            for (int i = 0; i < route.Nodes.Count; i++)
            {
                int voxel = route.Nodes[i];

                if (assignments[voxel] == Unallocated)
                {
                    selected.Add(voxel);
                    importance[voxel] += contribution;
                }

                if (i == 0)
                    continue;

                int a = Math.Min(route.Nodes[i - 1], voxel);
                int b = Math.Max(route.Nodes[i - 1], voxel);
                edges.Add(Tuple.Create(a, b));
            }
        }

        private static void AddRouteToGroundedTails(
            IReadOnlyList<int> route,
            Dictionary<int, IReadOnlyList<int>> tails)
        {
            for (int i = 0; i < route.Count; i++)
            {
                int node = route[i];
                if (!tails.ContainsKey(node))
                    tails[node] = route.Skip(i).ToList();
            }
        }

        private static PathRoute AppendGroundTail(
            PathRoute route,
            Dictionary<int, IReadOnlyList<int>> tails,
            IReadOnlyList<StructureVoxelKey> keys)
        {
            if (route.Nodes.Count == 0)
                return route;

            int endpoint = route.Nodes[route.Nodes.Count - 1];
            IReadOnlyList<int> tail;
            if (!tails.TryGetValue(endpoint, out tail) || tail.Count < 2)
                return route;

            var nodes = new List<int>(route.Nodes);
            for (int i = 1; i < tail.Count; i++)
                nodes.Add(tail[i]);

            double capacity = 1.0;
            for (int i = 1; i < nodes.Count; i++)
                capacity *= StepCapacity(nodes[i - 1], nodes[i], keys);

            return new PathRoute(nodes, route.Cost, capacity);
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
            StructureVoxelKey key,
            Dictionary<StructureVoxelKey, int> map,
            List<int> list)
        {
            int index;
            if (map.TryGetValue(key, out index))
                list.Add(index);
        }

        private static List<ProgramVoxelCluster> FindProgramClusters(
            IReadOnlyList<int> assignments,
            IReadOnlyList<List<int>> neighbours)
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

        private static PathRoute FindPath(
            int source,
            HashSet<int> ground,
            Dictionary<int, IReadOnlyList<int>> groundedTails,
            IReadOnlyList<StructureVoxelKey> keys,
            IReadOnlyList<List<int>> neighbours,
            int maximumCantilever)
        {
            if (ground.Count == 0)
                return null;

            var start = new RouteState(source, 0);
            var distances = new Dictionary<RouteState, double>();
            var previous = new Dictionary<RouteState, RouteState>();
            var visited = new HashSet<RouteState>();
            var queue = new List<QueueEntry>();

            distances[start] = 0.0;
            queue.Add(new QueueEntry(start, 0.0));
            RouteState destination = new RouteState(-1, 0);

            while (queue.Count > 0)
            {
                int best = 0;
                for (int i = 1; i < queue.Count; i++)
                {
                    if (queue[i].Cost < queue[best].Cost ||
                        (Math.Abs(queue[i].Cost - queue[best].Cost) < 1e-9 &&
                         CompareState(queue[i].State, queue[best].State) < 0))
                    {
                        best = i;
                    }
                }

                var entry = queue[best];
                queue.RemoveAt(best);

                if (!visited.Add(entry.State))
                    continue;

                IReadOnlyList<int> tail;
                if (ground.Contains(entry.State.Index) ||
                    (groundedTails.TryGetValue(entry.State.Index, out tail) &&
                     CanAppendTail(entry.State.HorizontalRun, tail, keys, maximumCantilever)))
                {
                    destination = entry.State;
                    break;
                }

                foreach (int next in neighbours[entry.State.Index])
                {
                    int dz = keys[next].Z - keys[entry.State.Index].Z;

                    // Upward support is never valid.
                    if (dz > 0)
                        continue;

                    int nextHorizontalRun;
                    if (dz == 0)
                    {
                        nextHorizontalRun = entry.State.HorizontalRun + 1;
                        if (nextHorizontalRun > maximumCantilever)
                            continue;
                    }
                    else
                    {
                        // A downward step resets the cantilever run.
                        nextHorizontalRun = 0;
                    }

                    var nextState = new RouteState(next, nextHorizontalRun);
                    if (visited.Contains(nextState))
                        continue;

                    double step = dz < 0 ? 1.0 : 4.0;
                    if (groundedTails.ContainsKey(next))
                        step *= 0.20;

                    double nextCost = entry.Cost + step;
                    double oldCost;
                    if (!distances.TryGetValue(nextState, out oldCost) ||
                        nextCost + 1e-9 < oldCost ||
                        (Math.Abs(nextCost - oldCost) < 1e-9 &&
                         CompareState(entry.State, previous.ContainsKey(nextState)
                             ? previous[nextState] : entry.State) < 0))
                    {
                        distances[nextState] = nextCost;
                        previous[nextState] = entry.State;
                        queue.Add(new QueueEntry(nextState, nextCost));
                    }
                }
            }

            if (destination.Index < 0)
                return null;

            var states = new List<RouteState>();
            var current = destination;
            states.Add(current);

            while (!current.Equals(start))
            {
                RouteState prior;
                if (!previous.TryGetValue(current, out prior))
                    return null;
                current = prior;
                states.Add(current);
            }

            states.Reverse();
            var nodes = states.Select(state => state.Index).ToList();

            double capacity = 1.0;
            for (int i = 1; i < nodes.Count; i++)
                capacity *= StepCapacity(nodes[i - 1], nodes[i], keys);

            return new PathRoute(nodes, distances[destination], capacity);
        }

        private static bool CanAppendTail(
            int horizontalRun,
            IReadOnlyList<int> tail,
            IReadOnlyList<StructureVoxelKey> keys,
            int maximumCantilever)
        {
            for (int i = 1; i < tail.Count; i++)
            {
                int dz = keys[tail[i]].Z - keys[tail[i - 1]].Z;
                if (dz > 0)
                    return false;

                if (dz == 0)
                {
                    horizontalRun++;
                    if (horizontalRun > maximumCantilever)
                        return false;
                }
                else
                {
                    horizontalRun = 0;
                }
            }

            return true;
        }

        private static int CompareState(RouteState a, RouteState b)
        {
            int compare = a.Index.CompareTo(b.Index);
            return compare != 0 ? compare : a.HorizontalRun.CompareTo(b.HorizontalRun);
        }

        private static double StepCapacity(
            int current,
            int next,
            IReadOnlyList<StructureVoxelKey> keys)
        {
            int dz = keys[next].Z - keys[current].Z;
            return dz < 0 ? 1.0 : 0.25;
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

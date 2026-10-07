// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;

namespace Cumulus
{
    /// <summary>Integer grid coordinate used by <see cref="SupportGrowthEngine"/>.</summary>
    public readonly struct VoxelKey : IEquatable<VoxelKey>
    {
        public int X { get; }
        public int Y { get; }
        public int Z { get; }

        public VoxelKey(int x, int y, int z) { X = x; Y = y; Z = z; }

        public bool Equals(VoxelKey other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object? obj) => obj is VoxelKey other && Equals(other);
        public override int GetHashCode()
        {
            unchecked { return ((X * 397) ^ Y) * 397 ^ Z; }
        }
    }

    public sealed class SupportGrowthRequest
    {
        public IReadOnlyList<VoxelKey> Keys { get; }
        public IReadOnlyList<int> ProgramIndices { get; }
        public IReadOnlyList<double> SupportSuitability { get; }
        public IReadOnlyCollection<int> CoreAnchorIndices { get; }
        public int SupportProgramIndex { get; }
        public double HorizontalStepPenalty { get; }
        public double AnalysisPenalty { get; }

        public SupportGrowthRequest(
            IReadOnlyList<VoxelKey> keys,
            IReadOnlyList<int> programIndices,
            IReadOnlyList<double> supportSuitability,
            IReadOnlyCollection<int>? coreAnchorIndices,
            int supportProgramIndex,
            double horizontalStepPenalty,
            double analysisPenalty)
        {
            Keys = keys ?? throw new ArgumentNullException(nameof(keys));
            ProgramIndices = programIndices ?? throw new ArgumentNullException(nameof(programIndices));
            SupportSuitability = supportSuitability ?? throw new ArgumentNullException(nameof(supportSuitability));
            CoreAnchorIndices = coreAnchorIndices ?? Array.Empty<int>();
            SupportProgramIndex = supportProgramIndex;
            HorizontalStepPenalty = horizontalStepPenalty;
            AnalysisPenalty = analysisPenalty;
        }
    }

    public sealed class SupportGrowthResult
    {
        public List<int> ProgramIndices { get; }
        public List<int> AddedSupportIndices { get; }
        public List<List<int>> UnresolvedComponents { get; }
        public int FloatingComponentCount { get; }
        public int ResolvedComponentCount { get; }

        internal SupportGrowthResult(List<int> programIndices, List<int> added,
            List<List<int>> unresolved, int floatingCount, int resolvedCount)
        {
            ProgramIndices = programIndices;
            AddedSupportIndices = added;
            UnresolvedComponents = unresolved;
            FloatingComponentCount = floatingCount;
            ResolvedComponentCount = resolvedCount;
        }
    }

    /// <summary>
    /// Pure face-connected support routing for an assigned voxel stack.
    /// Existing non-negative program indices and -2 core indices are occupied;
    /// only -1 cells can become new support. Ground is the lowest filled Z layer.
    /// </summary>
    public static class SupportGrowthEngine
    {
        private static readonly VoxelKey[] Directions =
        {
            new VoxelKey(1, 0, 0), new VoxelKey(-1, 0, 0),
            new VoxelKey(0, 1, 0), new VoxelKey(0, -1, 0),
            new VoxelKey(0, 0, 1), new VoxelKey(0, 0, -1)
        };

        public static SupportGrowthResult Run(SupportGrowthRequest request)
        {
            Validate(request);

            int count = request.Keys.Count;
            var indexByKey = new Dictionary<VoxelKey, int>(count);
            int bottomZ = int.MaxValue;
            for (int i = 0; i < count; i++)
            {
                if (indexByKey.ContainsKey(request.Keys[i]))
                    throw new ArgumentException("Voxel keys must be unique.", nameof(request));
                indexByKey.Add(request.Keys[i], i);
                bottomZ = Math.Min(bottomZ, request.Keys[i].Z);
            }

            var resultIndices = request.ProgramIndices.ToList();
            var occupied = new HashSet<int>();
            for (int i = 0; i < count; i++)
                if (resultIndices[i] != -1) occupied.Add(i);

            // Bottom-layer occupancy and explicit/core anchors are trusted structural seeds.
            var supported = new HashSet<int>();
            foreach (int i in occupied)
                if (request.Keys[i].Z == bottomZ || resultIndices[i] == -2 ||
                    request.CoreAnchorIndices.Contains(i))
                    supported.Add(i);

            FloodOccupied(supported, occupied, request.Keys, indexByKey);

            var floatingComponents = FindFloatingComponents(
                occupied, supported, request.Keys, indexByKey);

            var added = new List<int>();
            var unresolved = new List<List<int>>();
            int resolved = 0;

            foreach (var component in floatingComponents)
            {
                // A preceding route can have attached this component to the supported network.
                if (component.Any(supported.Contains))
                {
                    resolved++;
                    continue;
                }

                var path = FindLeastCostPath(component, supported, resultIndices,
                    request, indexByKey);

                if (path == null)
                {
                    unresolved.Add(component.OrderBy(i => i).ToList());
                    continue;
                }

                foreach (int i in path)
                {
                    if (resultIndices[i] == -1)
                    {
                        resultIndices[i] = request.SupportProgramIndex;
                        occupied.Add(i);
                        supported.Add(i);
                        added.Add(i);
                    }
                }

                foreach (int i in component) supported.Add(i);
                FloodOccupied(supported, occupied, request.Keys, indexByKey);
                resolved++;
            }

            return new SupportGrowthResult(resultIndices, added, unresolved,
                floatingComponents.Count, resolved);
        }

        private static void Validate(SupportGrowthRequest request)
        {
            if (request.Keys.Count == 0)
                throw new ArgumentException("At least one voxel is required.", nameof(request));
            if (request.ProgramIndices.Count != request.Keys.Count ||
                request.SupportSuitability.Count != request.Keys.Count)
                throw new ArgumentException(
                    "Keys, program indices, and support suitability must have equal lengths.",
                    nameof(request));
            if (request.SupportProgramIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(request),
                    "SupportProgramIndex must be non-negative.");
            if (request.HorizontalStepPenalty < 0.0 || request.AnalysisPenalty < 0.0)
                throw new ArgumentOutOfRangeException(nameof(request),
                    "Support penalties must be non-negative.");
        }

        private static List<List<int>> FindFloatingComponents(HashSet<int> occupied,
            HashSet<int> supported, IReadOnlyList<VoxelKey> keys,
            Dictionary<VoxelKey, int> indexByKey)
        {
            var visited = new HashSet<int>();
            var components = new List<List<int>>();

            foreach (int start in occupied.OrderBy(i => i))
            {
                if (supported.Contains(start) || !visited.Add(start)) continue;

                var component = new List<int>();
                var queue = new Queue<int>();
                queue.Enqueue(start);

                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    component.Add(current);
                    foreach (int next in Neighbours(current, keys, indexByKey))
                    {
                        if (occupied.Contains(next) && !supported.Contains(next) &&
                            visited.Add(next))
                            queue.Enqueue(next);
                    }
                }

                components.Add(component);
            }

            // Resolve large/high components first. This makes routes deterministic
            // and lets newly grounded material help lower components afterwards.
            return components
                .OrderByDescending(c => c.Max(i => keys[i].Z))
                .ThenBy(c => c.Min())
                .ToList();
        }

        private static List<int>? FindLeastCostPath(IReadOnlyCollection<int> source,
            HashSet<int> supported, List<int> programIndices,
            SupportGrowthRequest request, Dictionary<VoxelKey, int> indexByKey)
        {
            var distances = new Dictionary<int, double>();
            var previous = new Dictionary<int, int>();
            var queue = new MinQueue();

            foreach (int i in source)
            {
                distances[i] = 0.0;
                queue.Enqueue(i, 0.0);
            }

            while (queue.Count > 0)
            {
                queue.TryDequeue(out int current, out double currentCost);
                if (!distances.TryGetValue(current, out double known) ||
                    currentCost > known + 1e-12)
                    continue;

                if (supported.Contains(current) && !source.Contains(current))
                    return ReconstructPath(current, previous, source);

                foreach (int next in Neighbours(current, request.Keys, indexByKey))
                {
                    // Existing unsupported program voxels are not legal routes:
                    // they must independently gain a path to support.
                    if (programIndices[next] != -1 && !supported.Contains(next))
                        continue;

                    double candidate = currentCost +
                        StepCost(current, next, request);
                    if (!distances.TryGetValue(next, out double old) ||
                        candidate + 1e-12 < old)
                    {
                        distances[next] = candidate;
                        previous[next] = current;
                        queue.Enqueue(next, candidate);
                    }
                }
            }

            return null;
        }

        private static double StepCost(int from, int to, SupportGrowthRequest request)
        {
            bool horizontal = request.Keys[from].Z == request.Keys[to].Z;
            double suitability = request.SupportSuitability[to];
            if (double.IsNaN(suitability) || double.IsInfinity(suitability))
                suitability = 0.0;
            suitability = Math.Max(0.0, Math.Min(1.0, suitability));

            return 1.0 +
                (horizontal ? request.HorizontalStepPenalty : 0.0) +
                request.AnalysisPenalty * (1.0 - suitability);
        }

        private static List<int> ReconstructPath(int destination,
            Dictionary<int, int> previous, IReadOnlyCollection<int> source)
        {
            var path = new List<int>();
            int current = destination;
            while (!source.Contains(current))
            {
                path.Add(current);
                current = previous[current];
            }
            path.Reverse();
            return path;
        }

        private static void FloodOccupied(HashSet<int> supported, HashSet<int> occupied,
            IReadOnlyList<VoxelKey> keys, Dictionary<VoxelKey, int> indexByKey)
        {
            var queue = new Queue<int>(supported);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (int next in Neighbours(current, keys, indexByKey))
                    if (occupied.Contains(next) && supported.Add(next))
                        queue.Enqueue(next);
            }
        }

        private sealed class MinQueue
        {
            private readonly List<(int item, double priority)> _items =
                new List<(int item, double priority)>();

            public int Count => _items.Count;

            public void Enqueue(int item, double priority)
            {
                _items.Add((item, priority));
                int child = _items.Count - 1;
                while (child > 0)
                {
                    int parent = (child - 1) / 2;
                    if (_items[parent].priority <= _items[child].priority) break;
                    var swap = _items[parent];
                    _items[parent] = _items[child];
                    _items[child] = swap;
                    child = parent;
                }
            }

            public void TryDequeue(out int item, out double priority)
            {
                var root = _items[0];
                int last = _items.Count - 1;
                _items[0] = _items[last];
                _items.RemoveAt(last);

                int parent = 0;
                while (true)
                {
                    int left = parent * 2 + 1;
                    if (left >= _items.Count) break;
                    int right = left + 1;
                    int child = right < _items.Count &&
                        _items[right].priority < _items[left].priority ? right : left;
                    if (_items[parent].priority <= _items[child].priority) break;
                    var swap = _items[parent];
                    _items[parent] = _items[child];
                    _items[child] = swap;
                    parent = child;
                }

                item = root.item;
                priority = root.priority;
            }
        }

        private static IEnumerable<int> Neighbours(int index, IReadOnlyList<VoxelKey> keys,
            Dictionary<VoxelKey, int> indexByKey)
        {
            VoxelKey k = keys[index];
            foreach (VoxelKey d in Directions)
                if (indexByKey.TryGetValue(new VoxelKey(k.X + d.X, k.Y + d.Y, k.Z + d.Z),
                    out int neighbour))
                    yield return neighbour;
        }
    }
}
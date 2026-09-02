// -*- coding: utf-8 -*-
// Version 3.1.0
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace SpatialMorphology
{
    public class CoreLocationComponent : SAComponentBase
    {
        // ── Constructor ───────────────────────────────────────────────────────
        public CoreLocationComponent()
            : base(
                "CoreLocation",
                "Core",
                "Determines core locations in the voxel grid.\n\n" +
                "Mode 0 — Manual:\n" +
                "  Input one or more curves defining core paths.\n" +
                "  Each curve defines one independent core.\n\n" +
                "Mode 1 — Generative:\n" +
                "  Uses existing SA analysis channels as input scores.\n" +
                "  Dynamic programming finds optimal vertical core paths\n" +
                "  that minimize total travel distance for all voxels.\n" +
                "  Coverage uses BFS floor travel distance — disjointed\n" +
                "  floor islands always receive their own core.\n" +
                "  Additional cores added until all voxels within max_distance.\n\n" +
                "Output analysis = inverted distance to nearest core.\n" +
                "Closer to core = higher value.\n\n" +
                "Version 3.1.0",
                "Spatial Morphology",
                "Analysis")
        { }

        // ── GUID ──────────────────────────────────────────────────────────────
        public override Guid ComponentGuid =>
            new Guid("B8C9D0E1-F2A3-4567-BCDE-012345678916");

        // ── Icon ──────────────────────────────────────────────────────────────
        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "SpatialMorphology.Resources.CoreLocation_24.png");
                return stream != null ? new Bitmap(stream) : null;
            }
        }

        // ── Parameters ────────────────────────────────────────────────────────
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("voxel_grid", "VG",
                "VoxelGrid object from the VoxelGrid component.",
                GH_ParamAccess.item);
            pManager.AddIntegerParameter("mode", "M",
                "Analysis mode:\n" +
                "  0 = Manual      (use core_curves input)\n" +
                "  1 = Generative  (DP-based optimal core placement)",
                GH_ParamAccess.item, 0);
            pManager.AddCurveParameter("core_curves", "CC",
                "Manual mode only.\n" +
                "One or more curves defining core paths.\n" +
                "Each curve defines one independent core.",
                GH_ParamAccess.list);
            pManager.AddGenericParameter("analysis", "A",
                "Generative mode only. Optional.\n" +
                "List of SpatialAnalysis objects to use as scoring channels.\n" +
                "Higher channel values = better core candidate positions.\n" +
                "If not connected, travel distance alone is used for scoring.",
                GH_ParamAccess.list);
            pManager.AddNumberParameter("radius", "R",
                "Distance from core path that defines core voxels.\n" +
                "In model units. Default: 10.0.",
                GH_ParamAccess.item, 10.0);
            pManager.AddNumberParameter("max_distance", "MD",
                "Maximum allowed BFS floor travel distance from any voxel\n" +
                "to its nearest core. Additional cores added automatically\n" +
                "if any voxel exceeds this. Default: 100.0 (feet).",
                GH_ParamAccess.item, 100.0);
            pManager.AddIntegerParameter("min_island_size", "MI",
                "Minimum connected voxels in a floor island\n" +
                "to receive a core. Default: 4.",
                GH_ParamAccess.item, 4);
            pManager.AddTextParameter("label", "L",
                "Channel name used by AnalysisStack. Default: 'core'.",
                GH_ParamAccess.item, "core");

            pManager[2].Optional = true;
            pManager[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("analysis", "A",
                "SpatialAnalysis object. Wire into AnalysisStack.\n" +
                "Value = inverted distance to nearest core.\n" +
                "Closer to core = higher value.",
                GH_ParamAccess.item);
            pManager.AddNumberParameter("values", "V",
                "Per-voxel inverted distance to nearest core.",
                GH_ParamAccess.list);
            pManager.AddPointParameter("centers", "C",
                "Voxel centres for preview.",
                GH_ParamAccess.list);
            pManager.AddColourParameter("gradient", "G",
                "Per-voxel gradient color from low (red) to high (blue).",
                GH_ParamAccess.list);
            pManager.AddIntegerParameter("core_indices", "CI",
                "Voxel indices designated as core voxels.",
                GH_ParamAccess.list);
            pManager.AddBooleanParameter("is_core", "IC",
                "Per-voxel boolean. True = core voxel.\n" +
                "Parallel to voxel_grid.filled_keys.",
                GH_ParamAccess.list);
            pManager.AddCurveParameter("core_centerlines", "CL",
                "One continuous polyline per core from lowest to highest floor.",
                GH_ParamAccess.list);
            pManager.AddCurveParameter("core_footprints", "CF",
                "DataTree of floor rectangle outlines per core.\n" +
                "Branch {c} = rectangles for core c, one per floor.\n" +
                "Loft branches independently to create core geometry.",
                GH_ParamAccess.tree);
            pManager.AddTextParameter("info", "I",
                "Summary.",
                GH_ParamAccess.item);
        }

        // ── Solve ─────────────────────────────────────────────────────────────
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // ── Collect inputs ────────────────────────────────────────────────
            object voxelGridObj = null;
            int mode = 0;
            var inputCurves = new List<Curve>();
            var analysisObjs = new List<object>();
            double radius = 10.0;
            double maxDistance = 100.0;
            int minIslandSize = 4;
            string label = "core";

            if (!DA.GetData(0, ref voxelGridObj)) return;
            DA.GetData(1, ref mode);
            DA.GetDataList(2, inputCurves);
            DA.GetDataList(3, analysisObjs);
            DA.GetData(4, ref radius);
            DA.GetData(5, ref maxDistance);
            DA.GetData(6, ref minIslandSize);
            DA.GetData(7, ref label);

            // ── Unwrap VoxelGrid ──────────────────────────────────────────────
            var voxelGrid = UnwrapVoxelGrid(voxelGridObj);
            if (voxelGrid == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Could not read VoxelGrid object.");
                return;
            }

            // ── Validate ──────────────────────────────────────────────────────
            string resolvedLabel = string.IsNullOrWhiteSpace(label)
                ? "core" : label.Trim();
            mode = Math.Max(0, Math.Min(1, mode));
            radius = Math.Max(0.01, radius);
            maxDistance = Math.Max(1.0, maxDistance);
            minIslandSize = Math.Max(1, minIslandSize);

            var orderedKeys = voxelGrid.FilledKeys;
            int n = orderedKeys.Count;
            double vs = voxelGrid.VoxelSize;

            // ── Precompute world-space centers ────────────────────────────────
            var centers = orderedKeys
                .Select(key => voxelGrid.KeyToCenter(key))
                .ToList();

            // ── Build key-to-index map ────────────────────────────────────────
            var keyToIndex = new Dictionary<(int, int, int), int>(n);
            for (int i = 0; i < n; i++)
                keyToIndex[orderedKeys[i]] = i;

            // ── Group voxels by floor ─────────────────────────────────────────
            var voxelsByFloor = new SortedDictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int iz = orderedKeys[i].Item3;
                if (!voxelsByFloor.ContainsKey(iz))
                    voxelsByFloor[iz] = new List<int>();
                voxelsByFloor[iz].Add(i);
            }

            // ── Unwrap optional SA analysis channels ──────────────────────────
            var saChannels = new List<List<double>>();
            foreach (var obj in analysisObjs)
            {
                var inner = obj is GH_ObjectWrapper w ? w.Value : obj;
                List<double> vals = null;

                if (inner is SpatialAnalysis sa && sa.Values.Count == n)
                    vals = sa.Values.ToList();
                else if (inner != null)
                {
                    try
                    {
                        dynamic dyn = inner;
                        var dvls = new List<double>();
                        foreach (var v in dyn.values)
                            dvls.Add(Convert.ToDouble(v));
                        if (dvls.Count == n) vals = dvls;
                    }
                    catch { }
                }

                if (vals != null)
                {
                    double lo = vals.Min();
                    double hi = vals.Max();
                    double rng = hi - lo;
                    saChannels.Add(vals
                        .Select(v => rng > 1e-12 ? (v - lo) / rng : 0.0)
                        .ToList());
                }
            }

            // ── Core data ─────────────────────────────────────────────────────
            var coreFloorData = new List<List<(int floor,
                Point3d centroid, List<int> indices)>>();
            var coreVoxelSet = new HashSet<int>();

            // ── Mode 0 — Manual ───────────────────────────────────────────────
            if (mode == 0)
            {
                if (inputCurves.Count == 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        "Connect at least one curve to 'core_curves' in manual mode.");
                    return;
                }

                for (int c = 0; c < inputCurves.Count; c++)
                {
                    var curve = inputCurves[c];
                    if (curve == null) continue;

                    var floorGroups = new SortedDictionary<int, List<int>>();

                    for (int i = 0; i < n; i++)
                    {
                        double t;
                        curve.ClosestPoint(centers[i], out t);
                        double dist = centers[i].DistanceTo(curve.PointAt(t));

                        if (dist <= radius)
                        {
                            coreVoxelSet.Add(i);
                            int iz = orderedKeys[i].Item3;
                            if (!floorGroups.ContainsKey(iz))
                                floorGroups[iz] = new List<int>();
                            floorGroups[iz].Add(i);
                        }
                    }

                    var thisCore = new List<(int, Point3d, List<int>)>();
                    foreach (var kvp in floorGroups)
                    {
                        var cent = VoxelCentroid(kvp.Value, centers);
                        thisCore.Add((kvp.Key, cent, kvp.Value));
                    }
                    if (thisCore.Count > 0)
                        coreFloorData.Add(thisCore);
                }
            }
            // ── Mode 1 — Generative (DP + BFS coverage) ──────────────────────
            else
            {
                // ── Step 1: Compute per-voxel raw score ───────────────────────
                // Score = normalized avg travel dist - SA bonus
                // Lower = better core candidate
                var rawScore = new double[n];

                foreach (var kvp in voxelsByFloor)
                {
                    var fv = kvp.Value;

                    // Sample up to 50 voxels per floor for efficiency
                    var sample = fv.Count <= 50
                        ? fv
                        : fv.Take(50).ToList();

                    foreach (var idx in fv)
                    {
                        double avgDist = sample.Count > 0
                            ? sample.Average(s =>
                                centers[idx].DistanceTo(centers[s]))
                            : 0.0;

                        double saBonus = saChannels.Count > 0
                            ? saChannels.Average(ch => ch[idx])
                            : 0.0;

                        rawScore[idx] = (avgDist / maxDistance) - saBonus;
                    }
                }

                // ── Step 2: Vertical blending ─────────────────────────────────
                // blended = 0.60*current + 0.25*below + 0.15*above
                var blendedScore = new double[n];
                var floorList = voxelsByFloor.Keys.ToList();

                for (int fi = 0; fi < floorList.Count; fi++)
                {
                    var fv = voxelsByFloor[floorList[fi]];

                    List<int> belowVoxels = fi > 0
                        ? voxelsByFloor[floorList[fi - 1]]
                        : new List<int>();
                    List<int> aboveVoxels = fi < floorList.Count - 1
                        ? voxelsByFloor[floorList[fi + 1]]
                        : new List<int>();

                    foreach (var idx in fv)
                    {
                        int ix = orderedKeys[idx].Item1;
                        int iy = orderedKeys[idx].Item2;

                        double belowScore = GetNeighborScore(
                            ix, iy, belowVoxels, orderedKeys, rawScore);
                        double aboveScore = GetNeighborScore(
                            ix, iy, aboveVoxels, orderedKeys, rawScore);

                        blendedScore[idx] = 0.60 * rawScore[idx]
                                          + 0.25 * belowScore
                                          + 0.15 * aboveScore;
                    }
                }

                // ── Step 3: Iterative DP core placement ───────────────────────
                // Shift penalty auto-scaled: vertical always wins unless
                // shift saves > maxDistance/10 in travel
                double shiftPenalty = maxDistance / 10.0 / vs;
                var uncovered = new HashSet<int>(Enumerable.Range(0, n));
                int maxCores = 20;
                int coreIter = 0;

                while (uncovered.Count > 0 && coreIter < maxCores)
                {
                    coreIter++;

                    // Find which floors have uncovered voxels
                    var floorsWithUncovered = new HashSet<int>(
                        uncovered.Select(idx => orderedKeys[idx].Item3));

                    if (floorsWithUncovered.Count == 0) break;

                    // Use ALL floor voxels for path connectivity
                    // DP can traverse covered voxels to reach uncovered ones
                    var coreFloors = voxelsByFloor.Keys
                        .Where(f => floorsWithUncovered.Contains(f) ||
                            voxelsByFloor[f].Any(idx => !uncovered.Contains(idx)))
                        .OrderBy(f => f)
                        .ToList();

                    if (coreFloors.Count == 0) break;

                    // ── DP ────────────────────────────────────────────────────
                    var dp = new Dictionary<int, double>();
                    var parent = new Dictionary<int, int>();

                    // Seed bottom floor — prefer uncovered voxels
                    int botFloor = coreFloors.First(
                        f => floorsWithUncovered.Contains(f));

                    foreach (var idx in voxelsByFloor[botFloor])
                    {
                        // Score 0 for covered voxels — they are just path nodes
                        double score = uncovered.Contains(idx)
                            ? blendedScore[idx]
                            : 0.0;
                        dp[idx] = score;
                        parent[idx] = -1;
                    }

                    for (int fi = 1; fi < coreFloors.Count; fi++)
                    {
                        int curFloor = coreFloors[fi];
                        int prevFloor = coreFloors[fi - 1];

                        if (!voxelsByFloor.ContainsKey(prevFloor)) continue;

                        foreach (var curIdx in voxelsByFloor[curFloor])
                        {
                            int cix = orderedKeys[curIdx].Item1;
                            int ciy = orderedKeys[curIdx].Item2;

                            double bestCost = double.MaxValue;
                            int bestPrev = -1;

                            foreach (var prevIdx in voxelsByFloor[prevFloor])
                            {
                                if (!dp.ContainsKey(prevIdx)) continue;

                                int pix = orderedKeys[prevIdx].Item1;
                                int piy = orderedKeys[prevIdx].Item2;

                                int dix = Math.Abs(cix - pix);
                                int diy = Math.Abs(ciy - piy);

                                // Max 1 voxel horizontal shift
                                if (dix > 1 || diy > 1) continue;

                                double shift = Math.Sqrt(dix * dix + diy * diy);
                                double score = uncovered.Contains(curIdx)
                                    ? blendedScore[curIdx]
                                    : 0.0;
                                double cost = dp[prevIdx]
                                             + score
                                             + shift * shiftPenalty;

                                if (cost < bestCost)
                                {
                                    bestCost = cost;
                                    bestPrev = prevIdx;
                                }
                            }

                            if (bestPrev >= 0)
                            {
                                dp[curIdx] = bestCost;
                                parent[curIdx] = bestPrev;
                            }
                        }
                    }

                    // ── Trace back best path ───────────────────────────────────
                    // Find best endpoint on topmost floor with uncovered voxels
                    int topFloor = coreFloors.Last(
                        f => floorsWithUncovered.Contains(f));

                    int bestEnd = -1;
                    double bestDP = double.MaxValue;

                    foreach (var idx in voxelsByFloor[topFloor])
                    {
                        if (!dp.ContainsKey(idx)) continue;
                        if (!uncovered.Contains(idx)) continue;
                        if (dp[idx] < bestDP)
                        {
                            bestDP = dp[idx];
                            bestEnd = idx;
                        }
                    }

                    // Fallback — take any reachable voxel on top floor
                    if (bestEnd < 0)
                    {
                        foreach (var idx in voxelsByFloor[topFloor])
                        {
                            if (dp.ContainsKey(idx) && dp[idx] < bestDP)
                            {
                                bestDP = dp[idx];
                                bestEnd = idx;
                            }
                        }
                    }

                    // Last resort — best uncovered on bottom floor
                    if (bestEnd < 0)
                    {
                        bestEnd = voxelsByFloor[botFloor]
                            .Where(idx => uncovered.Contains(idx))
                            .OrderBy(idx => blendedScore[idx])
                            .FirstOrDefault();
                    }

                    if (bestEnd < 0) break;

                    // Trace path bottom to top
                    var pathIndices = new List<int>();
                    int cur = bestEnd;
                    int safety = n + 1;

                    while (cur >= 0 && safety-- > 0)
                    {
                        pathIndices.Add(cur);
                        cur = parent.ContainsKey(cur) ? parent[cur] : -1;
                    }

                    pathIndices.Reverse();

                    // ── Expand path to core voxels within radius ───────────────
                    var thisFloorData = new List<(int, Point3d, List<int>)>();
                    var pathByFloor = new SortedDictionary<int, int>();

                    foreach (var idx in pathIndices)
                    {
                        int iz = orderedKeys[idx].Item3;
                        if (!pathByFloor.ContainsKey(iz))
                            pathByFloor[iz] = idx;
                    }

                    foreach (var kvp in pathByFloor)
                    {
                        int floor = kvp.Key;
                        int seedIdx = kvp.Value;
                        var seedPt = centers[seedIdx];

                        var floorCore = voxelsByFloor.ContainsKey(floor)
                            ? voxelsByFloor[floor]
                                .Where(idx =>
                                    centers[idx].DistanceTo(seedPt) <= radius)
                                .ToList()
                            : new List<int> { seedIdx };

                        if (floorCore.Count == 0)
                            floorCore.Add(seedIdx);

                        foreach (var idx in floorCore)
                            coreVoxelSet.Add(idx);

                        thisFloorData.Add((floor,
                            VoxelCentroid(floorCore, centers),
                            floorCore));
                    }

                    if (thisFloorData.Count > 0)
                        coreFloorData.Add(thisFloorData);

                    // ── Update coverage using BFS floor travel distance ────────
                    var coverage = ComputeFloorBFSCoverage(
                        coreVoxelSet, voxelsByFloor, keyToIndex,
                        orderedKeys, centers, maxDistance);

                    uncovered.ExceptWith(coverage);
                    uncovered.ExceptWith(coreVoxelSet);
                }

                if (uncovered.Count > 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        string.Format(
                            "{0} voxels remain beyond max_distance={1:F1}.\n" +
                            "Try reducing max_distance or increasing radius.",
                            uncovered.Count, maxDistance));
            }

            // ── Build centerlines and footprints ──────────────────────────────
            var coreCenterlines = new List<Curve>();
            var coreFootprintTree = new GH_Structure<GH_Curve>();

            for (int c = 0; c < coreFloorData.Count; c++)
            {
                var path = new GH_Path(c);
                var floorData = coreFloorData[c]
                    .OrderBy(f => f.floor)
                    .ToList();

                if (floorData.Count == 0) continue;

                // ── One continuous centerline polyline ─────────────────────────
                var clPts = floorData.Select(f => f.centroid).ToList();

                if (clPts.Count == 1)
                {
                    var p0 = clPts[0];
                    coreCenterlines.Add(new LineCurve(p0,
                        new Point3d(p0.X, p0.Y, p0.Z + vs)));
                }
                else
                {
                    coreCenterlines.Add(
                        new Polyline(clPts).ToNurbsCurve());
                }

                // ── Floor footprints ───────────────────────────────────────────
                foreach (var (floor, centroid, floorIndices) in floorData)
                {
                    if (floorIndices.Count == 0) continue;

                    var pts = floorIndices.Select(idx => centers[idx]).ToList();
                    double minX = pts.Min(p => p.X);
                    double maxX = pts.Max(p => p.X);
                    double minY = pts.Min(p => p.Y);
                    double maxY = pts.Max(p => p.Y);
                    double avgZ = pts.Average(p => p.Z);

                    double hs = vs * 0.5;
                    minX -= hs; maxX += hs;
                    minY -= hs; maxY += hs;

                    var rectPts = new List<Point3d>
                    {
                        new Point3d(minX, minY, avgZ),
                        new Point3d(maxX, minY, avgZ),
                        new Point3d(maxX, maxY, avgZ),
                        new Point3d(minX, maxY, avgZ),
                        new Point3d(minX, minY, avgZ),
                    };

                    coreFootprintTree.Append(
                        new GH_Curve(new Polyline(rectPts).ToNurbsCurve()),
                        path);
                }
            }

            // ── Compute inverted distance to nearest core ─────────────────────
            var coreCenterPts = coreVoxelSet
                .Select(idx => centers[idx])
                .ToList();

            var raw = new List<double>(n);
            var isCore = new List<bool>(n);
            double maxDist = 0;

            for (int i = 0; i < n; i++)
            {
                if (coreVoxelSet.Contains(i))
                {
                    raw.Add(0.0);
                    isCore.Add(true);
                    continue;
                }

                isCore.Add(false);

                if (coreCenterPts.Count == 0)
                {
                    raw.Add(0.0);
                    continue;
                }

                double minDist = coreCenterPts
                    .Min(cp => centers[i].DistanceTo(cp));
                raw.Add(minDist);
                if (minDist > maxDist) maxDist = minDist;
            }

            var invertedRaw = raw.Select(d => maxDist - d).ToList();

            // ── Build analysis output ─────────────────────────────────────────
            var analysis = new SpatialAnalysis(resolvedLabel, invertedRaw);
            var gradient = ComputeGradient(invertedRaw);
            BuildPreviewData(voxelGrid, invertedRaw);

            var coreIndicesList = coreVoxelSet.OrderBy(i => i).ToList();

            // ── Final BFS coverage for info ───────────────────────────────────
            var finalCoverage = ComputeFloorBFSCoverage(
                coreVoxelSet, voxelsByFloor, keyToIndex,
                orderedKeys, centers, maxDistance);
            int nBeyond = Enumerable.Range(0, n)
                .Count(i => !finalCoverage.Contains(i) &&
                            !coreVoxelSet.Contains(i));

            // ── Info ──────────────────────────────────────────────────────────
            string[] modeNames = { "Manual", "Generative (DP + BFS)" };
            string info = string.Format(
                "CoreLocation | mode={0} ({1}) | voxels={2}\n" +
                "cores={3} | core_voxels={4} ({5:F1}% of grid)\n" +
                "max_distance={6:F1} (BFS floor travel) | voxels_beyond={7}\n" +
                "max_actual_dist={8:F2} | sa_channels={9}\n" +
                "shift_penalty={10:F3} (auto-scaled)",
                mode, modeNames[mode],
                n,
                coreFloorData.Count,
                coreVoxelSet.Count,
                n > 0 ? (double)coreVoxelSet.Count / n * 100.0 : 0,
                maxDistance, nBeyond,
                maxDist,
                saChannels.Count,
                mode == 1 ? maxDistance / 10.0 / vs : 0.0);

            // ── Output ────────────────────────────────────────────────────────
            DA.SetData(0, analysis);
            DA.SetDataList(1, invertedRaw);
            DA.SetDataList(2, centers);
            DA.SetDataList(3, gradient);
            DA.SetDataList(4, coreIndicesList);
            DA.SetDataList(5, isCore);
            DA.SetDataList(6, coreCenterlines);
            DA.SetDataTree(7, coreFootprintTree);
            DA.SetData(8, info);
        }

        // ── BFS floor travel coverage ─────────────────────────────────────────
        // Returns set of voxel indices reachable from any core voxel
        // within maxDistance by walking along connected floor voxels.
        // Disjointed islands with no core voxel are NOT covered.
        private HashSet<int> ComputeFloorBFSCoverage(
            HashSet<int> coreVoxelSet,
            SortedDictionary<int, List<int>> voxelsByFloor,
            Dictionary<(int, int, int), int> keyToIndex,
            IReadOnlyList<(int, int, int)> orderedKeys,
            List<Point3d> centers,
            double maxDistance)
        {
            var covered = new HashSet<int>();

            int[] dix = { 1, -1, 0, 0 };
            int[] diy = { 0, 0, 1, -1 };

            foreach (var kvp in voxelsByFloor)
            {
                int floor = kvp.Key;
                var floorVoxels = new HashSet<int>(kvp.Value);

                // Find core voxels on this floor
                var floorCores = floorVoxels
                    .Where(idx => coreVoxelSet.Contains(idx))
                    .ToList();

                if (floorCores.Count == 0) continue;

                // Multi-source BFS from all core voxels on this floor
                var dist = new Dictionary<int, double>();
                var queue = new Queue<int>();

                foreach (var idx in floorCores)
                {
                    dist[idx] = 0.0;
                    queue.Enqueue(idx);
                    covered.Add(idx);
                }

                while (queue.Count > 0)
                {
                    int curr = queue.Dequeue();
                    double curD = dist[curr];

                    int ix = orderedKeys[curr].Item1;
                    int iy = orderedKeys[curr].Item2;
                    int iz = orderedKeys[curr].Item3;

                    for (int d = 0; d < 4; d++)
                    {
                        var nbKey = (ix + dix[d], iy + diy[d], iz);
                        if (!keyToIndex.ContainsKey(nbKey)) continue;
                        int nbIdx = keyToIndex[nbKey];
                        if (!floorVoxels.Contains(nbIdx)) continue;
                        if (dist.ContainsKey(nbIdx)) continue;

                        double newDist = curD +
                            centers[curr].DistanceTo(centers[nbIdx]);

                        if (newDist <= maxDistance)
                        {
                            dist[nbIdx] = newDist;
                            queue.Enqueue(nbIdx);
                            covered.Add(nbIdx);
                        }
                    }
                }
            }

            return covered;
        }

        // ── Get neighbor score for vertical blending ──────────────────────────
        private double GetNeighborScore(
            int ix, int iy,
            List<int> neighborVoxels,
            IReadOnlyList<(int, int, int)> orderedKeys,
            double[] rawScore)
        {
            if (neighborVoxels.Count == 0) return 0.0;

            var nearby = neighborVoxels
                .Where(idx =>
                    Math.Abs(orderedKeys[idx].Item1 - ix) <= 1 &&
                    Math.Abs(orderedKeys[idx].Item2 - iy) <= 1)
                .ToList();

            return nearby.Count > 0
                ? nearby.Average(idx => rawScore[idx])
                : rawScore[neighborVoxels[0]];
        }

        // ── Centroid of a list of voxel indices ───────────────────────────────
        private Point3d VoxelCentroid(List<int> indices, List<Point3d> centers)
        {
            if (indices.Count == 0) return Point3d.Origin;
            double cx = 0, cy = 0, cz = 0;
            foreach (var idx in indices)
            {
                cx += centers[idx].X;
                cy += centers[idx].Y;
                cz += centers[idx].Z;
            }
            return new Point3d(cx / indices.Count,
                               cy / indices.Count,
                               cz / indices.Count);
        }
    }
}
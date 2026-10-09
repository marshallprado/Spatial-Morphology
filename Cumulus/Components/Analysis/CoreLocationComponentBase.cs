// -*- coding: utf-8 -*-
// Version 4.0.0
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace Cumulus
{
    /// <summary>
    /// Shared implementation for the focused manual and generative core components.
    /// This abstract class is not a Grasshopper canvas component.
    /// </summary>
    public abstract class CoreLocationComponentBase : SAComponentBase
    {
        private const int MaxCores = 20;

        /// <summary>
        /// Selects the input contract and core-placement algorithm supplied by
        /// the concrete component.
        /// </summary>
        protected abstract bool IsGenerative { get; }

        protected CoreLocationComponentBase(
            string name,
            string nickname,
            string description)
            : base(name, nickname, description, "Cumulus", "2 | Analysis")
        {
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "voxel_grid", "VG",
                "VoxelGrid object from the VoxelGrid component.",
                GH_ParamAccess.item);

            if (IsGenerative)
            {
                pManager.AddGenericParameter(
                    "analysis", "A",
                    "Optional SpatialAnalysis channels that attract generated core paths. " +
                    "Higher normalized values are preferred.",
                    GH_ParamAccess.list);

                pManager.AddNumberParameter(
                    "radius", "R",
                    "Distance from the core path that counts as core, in model units.",
                    GH_ParamAccess.item, 10.0);

                pManager.AddNumberParameter(
                    "max_distance", "MD",
                    "Maximum face-connected floor-travel distance to a core. " +
                    "Additional cores are generated until this target is reached " +
                    "or the internal core limit is reached.",
                    GH_ParamAccess.item, 100.0);

                pManager.AddIntegerParameter(
                    "min_island_size", "MI",
                    "Minimum connected floor-island size considered during generation.",
                    GH_ParamAccess.item, 4);

                pManager.AddBooleanParameter(
                    "invert", "I",
                    "False: near core scores high. True: far from core scores high.",
                    GH_ParamAccess.item, false);

                pManager.AddTextParameter(
                    "label", "L",
                    "Analysis-channel label used by AnalysisStack.",
                    GH_ParamAccess.item, "core");

                pManager[1].Optional = true;
            }
            else
            {
                pManager.AddCurveParameter(
                    "core_curves", "CC",
                    "One or more curves defining manual core paths.",
                    GH_ParamAccess.list);

                pManager.AddNumberParameter(
                    "radius", "R",
                    "Distance from the core path that counts as core, in model units.",
                    GH_ParamAccess.item, 10.0);

                pManager.AddBooleanParameter(
                    "invert", "I",
                    "False: near core scores high. True: far from core scores high.",
                    GH_ParamAccess.item, false);

                pManager.AddTextParameter(
                    "label", "L",
                    "Analysis-channel label used by AnalysisStack.",
                    GH_ParamAccess.item, "core");

                pManager[1].Optional = true;
            }
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("analysis", "A",
                "SpatialAnalysis object. Wire into AnalysisStack.\n" +
                "invert=False: near core = high value.\n" +
                "invert=True:  near core = low value.",
                GH_ParamAccess.item);
            pManager.AddNumberParameter("values", "V",
                "Per-voxel core proximity score, reversed if invert is true.",
                GH_ParamAccess.list);
            pManager.AddPointParameter("centers", "C",
                "Voxel centres for preview.",
                GH_ParamAccess.list);
            pManager.AddColourParameter("gradient", "G",
                "Per-voxel gradient color from low (red) to high (blue),\n" +
                "based on the output values.",
                GH_ParamAccess.list);
            pManager.AddIntegerParameter("core_indices", "CI",
                "Indices of voxels designated as core. Never inverted.",
                GH_ParamAccess.list);
            pManager.AddBooleanParameter("is_core", "IC",
                "Per-voxel boolean, True = core voxel.\n" +
                "Parallel to voxel_grid.FilledKeys. Never inverted.",
                GH_ParamAccess.list);
            pManager.AddCurveParameter("core_centerlines", "CL",
                "One polyline per core, lowest floor to highest.",
                GH_ParamAccess.list);
            pManager.AddCurveParameter("core_footprints", "CF",
                "DataTree of floor rectangles per core.\n" +
                "Branch {c} holds one rectangle per floor for core c.\n" +
                "Loft each branch independently to build core geometry.",
                GH_ParamAccess.tree);
            pManager.AddTextParameter("info", "I",
                "Summary.",
                GH_ParamAccess.item);
        }

        // -- Solve -------------------------------------------------------------
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            object voxelGridObj = null;
            var inputCurves = new List<Curve>();
            var analysisObjs = new List<object>();
            double radius = 10.0;
            double maxDistance = 100.0;
            int minIslandSize = 4;
            bool invert = false;
            string label = "core";

            if (!DA.GetData(0, ref voxelGridObj))
                return;

            if (IsGenerative)
            {
                DA.GetDataList(1, analysisObjs);
                DA.GetData(2, ref radius);
                DA.GetData(3, ref maxDistance);
                DA.GetData(4, ref minIslandSize);
                DA.GetData(5, ref invert);
                DA.GetData(6, ref label);
            }
            else
            {
                DA.GetDataList(1, inputCurves);
                DA.GetData(2, ref radius);
                DA.GetData(3, ref invert);
                DA.GetData(4, ref label);
            }

            var voxelGrid = UnwrapVoxelGrid(voxelGridObj);
            if (voxelGrid == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Could not read VoxelGrid object.");
                return;
            }

            string resolvedLabel = string.IsNullOrWhiteSpace(label)
                ? "core" : label.Trim();

            radius = Math.Max(0.01, radius);
            maxDistance = Math.Max(1.0, maxDistance);
            minIslandSize = Math.Max(1, minIslandSize);

            var orderedKeys = voxelGrid.FilledKeys;
            int n = orderedKeys.Count;
            double vs = voxelGrid.VoxelSize;

            if (n == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "VoxelGrid contains no filled voxels.");
                return;
            }

            // -- Precompute centres and lookup ---------------------------------
            var centers = new List<Point3d>(n);
            foreach (var key in orderedKeys)
                centers.Add(voxelGrid.KeyToCenter(key));

            var keyToIndex = new Dictionary<(int, int, int), int>(n);
            for (int i = 0; i < n; i++)
                keyToIndex[orderedKeys[i]] = i;

            // -- Group voxels by floor (grid Z layer) --------------------------
            var voxelsByFloor = new SortedDictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int iz = orderedKeys[i].Item3;
                if (!voxelsByFloor.ContainsKey(iz))
                    voxelsByFloor[iz] = new List<int>();
                voxelsByFloor[iz].Add(i);
            }

            // -- Unwrap and normalise optional scoring channels ----------------
            var saChannels = UnwrapChannels(analysisObjs, n);

            var coreFloorData =
                new List<List<(int floor, Point3d centroid, List<int> indices)>>();
            var coreVoxelSet = new HashSet<int>();

            // -- Mode 0 — Manual -----------------------------------------------
            if (!IsGenerative)
            {
                if (inputCurves.Count == 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        "Connect at least one curve to CC.");
                    return;
                }

                BuildManualCores(
                    inputCurves, centers, orderedKeys, radius,
                    coreVoxelSet, coreFloorData);

                if (coreVoxelSet.Count == 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        "No voxels fell within 'radius' of any curve.\n" +
                        "Check that the curves pass through the voxel grid and\n" +
                        "that radius is large enough for the voxel size.");
            }
            // -- Mode 1 — Generative -------------------------------------------
            else
            {
                int nUncovered = BuildGenerativeCores(
                    n, vs, radius, maxDistance, minIslandSize,
                    centers, orderedKeys, keyToIndex, voxelsByFloor,
                    saChannels, coreVoxelSet, coreFloorData);

                if (nUncovered > 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        string.Format(
                            "{0} voxels remain beyond max_distance = {1:F1}.\n" +
                            "Reduce max_distance, increase radius, or accept\n" +
                            "that isolated floor islands cannot all be served.",
                            nUncovered, maxDistance));
            }

            // -- Build centerlines and footprints ------------------------------
            var coreCenterlines = new List<Curve>();
            var coreFootprintTree = new GH_Structure<GH_Curve>();

            BuildCoreGeometry(
                coreFloorData, centers, vs,
                coreCenterlines, coreFootprintTree);

            // -- Distance to nearest core voxel --------------------------------
            var coreCenterPts = new List<Point3d>(coreVoxelSet.Count);
            foreach (var idx in coreVoxelSet)
                coreCenterPts.Add(centers[idx]);

            var rawDistance = new List<double>(n);
            var isCore = new List<bool>(n);
            double maxDist = 0.0;

            for (int i = 0; i < n; i++)
            {
                if (coreVoxelSet.Contains(i))
                {
                    rawDistance.Add(0.0);
                    isCore.Add(true);
                    continue;
                }

                isCore.Add(false);

                if (coreCenterPts.Count == 0)
                {
                    rawDistance.Add(0.0);
                    continue;
                }

                double minDist = double.MaxValue;
                foreach (var cp in coreCenterPts)
                {
                    double d = centers[i].DistanceTo(cp);
                    if (d < minDist) minDist = d;
                }

                rawDistance.Add(minDist);
                if (minDist > maxDist) maxDist = minDist;
            }

            // -- Default orientation: near core = HIGH -------------------------
            // This flip is what makes this component's invert convention the
            // reverse of the others. See the class remarks.
            var nearCoreHigh = new List<double>(n);
            foreach (var d in rawDistance)
                nearCoreHigh.Add(maxDist - d);

            // invert = true returns plain distance-from-core.
            var outputValues = invert ? InvertValues(nearCoreHigh) : nearCoreHigh;

            var analysis = new SpatialAnalysis(resolvedLabel, outputValues);
            var gradient = ComputeGradient(outputValues);
            BuildPreviewData(voxelGrid, outputValues);

            var coreIndicesList = coreVoxelSet.OrderBy(i => i).ToList();

            // -- Final coverage check for reporting ----------------------------
            var finalCoverage = ComputeFloorBFSCoverage(
                coreVoxelSet, voxelsByFloor, keyToIndex,
                orderedKeys, centers, maxDistance);

            int nBeyond = 0;
            for (int i = 0; i < n; i++)
                if (!finalCoverage.Contains(i) && !coreVoxelSet.Contains(i))
                    nBeyond++;

            // -- Info ----------------------------------------------------------
            double outMin = outputValues.Count > 0 ? outputValues[0] : 0.0;
            double outMax = outMin;
            foreach (var v in outputValues)
            {
                if (v < outMin) outMin = v;
                if (v > outMax) outMax = v;
            }

            string info = string.Format(
                "{0} | label='{1}' | voxels={2}\n" +
                "cores={3} | core_voxels={4} ({5:F1}% of grid)\n" +
                "max_distance={6:F1} (BFS floor travel) | voxels_beyond={7}\n" +
                "max_actual_dist={8:F2} | sa_channels={9}\n" +
                "invert={10} — {11}\n" +
                "output=[{12:F3} to {13:F3}]{14}",
                Name,
                resolvedLabel,
                n,
                coreFloorData.Count,
                coreVoxelSet.Count,
                n > 0 ? (double)coreVoxelSet.Count / n * 100.0 : 0.0,
                maxDistance,
                nBeyond,
                maxDist,
                saChannels.Count,
                invert,
                invert ? "far from core scores HIGH"
                       : "near core scores HIGH (default)",
                outMin,
                outMax,
                IsGenerative
                    ? string.Format("\nshift_penalty={0:F3} (auto-scaled)",
                        maxDistance / 10.0 / vs)
                    : "");

            DA.SetData(0, analysis);
            DA.SetDataList(1, outputValues);
            DA.SetDataList(2, centers);
            DA.SetDataList(3, gradient);
            DA.SetDataList(4, coreIndicesList);
            DA.SetDataList(5, isCore);
            DA.SetDataList(6, coreCenterlines);
            DA.SetDataTree(7, coreFootprintTree);
            DA.SetData(8, info);
        }

        // -- Channel unwrapping ------------------------------------------------
        // Each channel is normalised to 0..1 independently so that channels on
        // different scales bias the search evenly.
        private List<List<double>> UnwrapChannels(
            List<object> analysisObjs, int n)
        {
            var result = new List<List<double>>();

            foreach (var obj in analysisObjs)
            {
                var inner = obj is GH_ObjectWrapper w ? w.Value : obj;
                List<double> vals = null;

                if (inner is SpatialAnalysis sa && sa.Values.Count == n)
                {
                    vals = new List<double>(sa.Values);
                }
                else if (inner != null)
                {
                    try
                    {
                        dynamic dyn = inner;
                        var collected = new List<double>();
                        foreach (var v in dyn.values)
                            collected.Add(Convert.ToDouble(v));
                        if (collected.Count == n) vals = collected;
                    }
                    catch { }
                }

                if (vals == null) continue;

                double lo = vals[0], hi = vals[0];
                foreach (var v in vals)
                {
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                }

                double range = hi - lo;
                var normalised = new List<double>(n);
                foreach (var v in vals)
                    normalised.Add(range > 1e-12 ? (v - lo) / range : 0.0);

                result.Add(normalised);
            }

            return result;
        }

        // -- Mode 0 — cores from curves ----------------------------------------
        private void BuildManualCores(
            List<Curve> inputCurves,
            List<Point3d> centers,
            IReadOnlyList<(int, int, int)> orderedKeys,
            double radius,
            HashSet<int> coreVoxelSet,
            List<List<(int, Point3d, List<int>)>> coreFloorData)
        {
            foreach (var curve in inputCurves)
            {
                if (curve == null) continue;

                var floorGroups = new SortedDictionary<int, List<int>>();

                for (int i = 0; i < centers.Count; i++)
                {
                    double t;
                    if (!curve.ClosestPoint(centers[i], out t)) continue;

                    double dist = centers[i].DistanceTo(curve.PointAt(t));
                    if (dist > radius) continue;

                    coreVoxelSet.Add(i);

                    int iz = orderedKeys[i].Item3;
                    if (!floorGroups.ContainsKey(iz))
                        floorGroups[iz] = new List<int>();
                    floorGroups[iz].Add(i);
                }

                var thisCore = new List<(int, Point3d, List<int>)>();
                foreach (var kvp in floorGroups)
                    thisCore.Add((kvp.Key, VoxelCentroid(kvp.Value, centers), kvp.Value));

                if (thisCore.Count > 0)
                    coreFloorData.Add(thisCore);
            }
        }

        // -- Mode 1 — DP core search -------------------------------------------
        // Returns the number of voxels still uncovered when the search stops.
        private int BuildGenerativeCores(
            int n, double vs, double radius, double maxDistance, int minIslandSize,
            List<Point3d> centers,
            IReadOnlyList<(int, int, int)> orderedKeys,
            Dictionary<(int, int, int), int> keyToIndex,
            SortedDictionary<int, List<int>> voxelsByFloor,
            List<List<double>> saChannels,
            HashSet<int> coreVoxelSet,
            List<List<(int, Point3d, List<int>)>> coreFloorData)
        {
            // -- Per-voxel raw score. Lower is a better core candidate. --------
            var rawScore = new double[n];

            foreach (var kvp in voxelsByFloor)
            {
                var floorVoxels = kvp.Value;

                // Sampling keeps this tractable on large floors. The average
                // distance to a sample stands in for average travel distance.
                var sample = floorVoxels.Count <= 50
                    ? floorVoxels
                    : floorVoxels.Take(50).ToList();

                foreach (var idx in floorVoxels)
                {
                    double avgDist = 0.0;
                    if (sample.Count > 0)
                    {
                        foreach (var s in sample)
                            avgDist += centers[idx].DistanceTo(centers[s]);
                        avgDist /= sample.Count;
                    }

                    double saBonus = 0.0;
                    if (saChannels.Count > 0)
                    {
                        foreach (var ch in saChannels) saBonus += ch[idx];
                        saBonus /= saChannels.Count;
                    }

                    rawScore[idx] = (avgDist / maxDistance) - saBonus;
                }
            }

            // -- Vertical blending so cores prefer to stack --------------------
            var blendedScore = new double[n];
            var floorList = voxelsByFloor.Keys.ToList();

            for (int fi = 0; fi < floorList.Count; fi++)
            {
                var floorVoxels = voxelsByFloor[floorList[fi]];

                var below = fi > 0
                    ? voxelsByFloor[floorList[fi - 1]] : new List<int>();
                var above = fi < floorList.Count - 1
                    ? voxelsByFloor[floorList[fi + 1]] : new List<int>();

                foreach (var idx in floorVoxels)
                {
                    int ix = orderedKeys[idx].Item1;
                    int iy = orderedKeys[idx].Item2;

                    double belowScore = GetNeighborScore(ix, iy, below, orderedKeys, rawScore);
                    double aboveScore = GetNeighborScore(ix, iy, above, orderedKeys, rawScore);

                    blendedScore[idx] = 0.60 * rawScore[idx]
                                      + 0.25 * belowScore
                                      + 0.15 * aboveScore;
                }
            }

            // -- Iterative placement -------------------------------------------
            // Shift penalty auto-scales so a horizontal jog only wins if it
            // saves more than maxDistance/10 of travel.
            double shiftPenalty = maxDistance / 10.0 / vs;

            var uncovered = new HashSet<int>(Enumerable.Range(0, n));
            int coreIter = 0;

            while (uncovered.Count > 0 && coreIter < MaxCores)
            {
                coreIter++;

                var floorsWithUncovered = new HashSet<int>();
                foreach (var idx in uncovered)
                    floorsWithUncovered.Add(orderedKeys[idx].Item3);

                if (floorsWithUncovered.Count == 0) break;

                // Include fully covered floors as traversable path nodes, so a
                // core can run through served levels to reach unserved ones.
                var coreFloors = voxelsByFloor.Keys
                    .Where(f => floorsWithUncovered.Contains(f) ||
                                voxelsByFloor[f].Any(idx => !uncovered.Contains(idx)))
                    .OrderBy(f => f)
                    .ToList();

                if (coreFloors.Count == 0) break;

                var dp = new Dictionary<int, double>();
                var parent = new Dictionary<int, int>();

                int botFloor = coreFloors.First(f => floorsWithUncovered.Contains(f));

                foreach (var idx in voxelsByFloor[botFloor])
                {
                    // Covered voxels contribute no score — they are pure path.
                    dp[idx] = uncovered.Contains(idx) ? blendedScore[idx] : 0.0;
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

                            int dix = Math.Abs(cix - orderedKeys[prevIdx].Item1);
                            int diy = Math.Abs(ciy - orderedKeys[prevIdx].Item2);

                            // At most one voxel of horizontal drift per floor,
                            // so cores stay buildable rather than staircasing.
                            if (dix > 1 || diy > 1) continue;

                            double shift = Math.Sqrt(dix * dix + diy * diy);
                            double score = uncovered.Contains(curIdx)
                                ? blendedScore[curIdx] : 0.0;

                            double cost = dp[prevIdx] + score + shift * shiftPenalty;

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

                // -- Pick the best endpoint and trace back ---------------------
                int topFloor = coreFloors.Last(f => floorsWithUncovered.Contains(f));

                int bestEnd = -1;
                double bestDP = double.MaxValue;

                foreach (var idx in voxelsByFloor[topFloor])
                {
                    if (!dp.ContainsKey(idx) || !uncovered.Contains(idx)) continue;
                    if (dp[idx] < bestDP) { bestDP = dp[idx]; bestEnd = idx; }
                }

                if (bestEnd < 0)
                {
                    foreach (var idx in voxelsByFloor[topFloor])
                    {
                        if (!dp.ContainsKey(idx)) continue;
                        if (dp[idx] < bestDP) { bestDP = dp[idx]; bestEnd = idx; }
                    }
                }

                if (bestEnd < 0)
                {
                    var fallback = voxelsByFloor[botFloor]
                        .Where(idx => uncovered.Contains(idx))
                        .OrderBy(idx => blendedScore[idx])
                        .ToList();
                    if (fallback.Count == 0) break;
                    bestEnd = fallback[0];
                }

                var pathIndices = new List<int>();
                int cur = bestEnd;
                int safety = n + 1;

                while (cur >= 0 && safety-- > 0)
                {
                    pathIndices.Add(cur);
                    cur = parent.ContainsKey(cur) ? parent[cur] : -1;
                }
                pathIndices.Reverse();

                // -- Expand the path into core voxels --------------------------
                var thisFloorData = new List<(int, Point3d, List<int>)>();
                var pathByFloor = new SortedDictionary<int, int>();

                foreach (var idx in pathIndices)
                {
                    int iz = orderedKeys[idx].Item3;
                    if (!pathByFloor.ContainsKey(iz)) pathByFloor[iz] = idx;
                }

                foreach (var kvp in pathByFloor)
                {
                    int floor = kvp.Key;
                    var seedPt = centers[kvp.Value];

                    var floorCore = new List<int>();
                    if (voxelsByFloor.ContainsKey(floor))
                    {
                        foreach (var idx in voxelsByFloor[floor])
                            if (centers[idx].DistanceTo(seedPt) <= radius)
                                floorCore.Add(idx);
                    }

                    if (floorCore.Count == 0) floorCore.Add(kvp.Value);

                    foreach (var idx in floorCore) coreVoxelSet.Add(idx);

                    thisFloorData.Add((floor, VoxelCentroid(floorCore, centers), floorCore));
                }

                if (thisFloorData.Count > 0)
                    coreFloorData.Add(thisFloorData);

                var coverage = ComputeFloorBFSCoverage(
                    coreVoxelSet, voxelsByFloor, keyToIndex,
                    orderedKeys, centers, maxDistance);

                int before = uncovered.Count;
                uncovered.ExceptWith(coverage);
                uncovered.ExceptWith(coreVoxelSet);

                // No progress means the remainder is unreachable — adding more
                // cores would loop without ever satisfying max_distance.
                if (uncovered.Count == before) break;
            }

            return uncovered.Count;
        }

        // -- Centerlines and floor footprints ----------------------------------
        private void BuildCoreGeometry(
            List<List<(int floor, Point3d centroid, List<int> indices)>> coreFloorData,
            List<Point3d> centers,
            double vs,
            List<Curve> coreCenterlines,
            GH_Structure<GH_Curve> coreFootprintTree)
        {
            for (int c = 0; c < coreFloorData.Count; c++)
            {
                var path = new GH_Path(c);
                var floorData = coreFloorData[c].OrderBy(f => f.floor).ToList();
                if (floorData.Count == 0) continue;

                // -- Centerline ------------------------------------------------
                var clPts = floorData.Select(f => f.centroid).ToList();

                if (clPts.Count == 1)
                {
                    var p0 = clPts[0];
                    coreCenterlines.Add(new LineCurve(
                        p0, new Point3d(p0.X, p0.Y, p0.Z + vs)));
                }
                else
                {
                    coreCenterlines.Add(new Polyline(clPts).ToNurbsCurve());
                }

                // -- Per-floor bounding rectangles -----------------------------
                foreach (var (floor, centroid, floorIndices) in floorData)
                {
                    if (floorIndices.Count == 0) continue;

                    double minX = double.MaxValue, maxX = double.MinValue;
                    double minY = double.MaxValue, maxY = double.MinValue;
                    double sumZ = 0.0;

                    foreach (var idx in floorIndices)
                    {
                        var p = centers[idx];
                        if (p.X < minX) minX = p.X;
                        if (p.X > maxX) maxX = p.X;
                        if (p.Y < minY) minY = p.Y;
                        if (p.Y > maxY) maxY = p.Y;
                        sumZ += p.Z;
                    }

                    double avgZ = sumZ / floorIndices.Count;

                    // Grow by half a voxel so the rectangle wraps the voxel
                    // bodies rather than threading their centres.
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
                        new GH_Curve(new Polyline(rectPts).ToNurbsCurve()), path);
                }
            }
        }

        // -- BFS coverage along each floor -------------------------------------
        // Walks outward from core voxels through connected floor voxels only.
        // An island with no core voxel is never reached, which is the point —
        // straight-line distance would wrongly report it as served.
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
                var floorVoxels = new HashSet<int>(kvp.Value);

                var floorCores = new List<int>();
                foreach (var idx in floorVoxels)
                    if (coreVoxelSet.Contains(idx)) floorCores.Add(idx);

                if (floorCores.Count == 0) continue;

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

                        double newDist = curD + centers[curr].DistanceTo(centers[nbIdx]);
                        if (newDist > maxDistance) continue;

                        dist[nbIdx] = newDist;
                        queue.Enqueue(nbIdx);
                        covered.Add(nbIdx);
                    }
                }
            }

            return covered;
        }

        // -- Average score of vertically adjacent voxels -----------------------
        private double GetNeighborScore(
            int ix, int iy,
            List<int> neighborVoxels,
            IReadOnlyList<(int, int, int)> orderedKeys,
            double[] rawScore)
        {
            if (neighborVoxels.Count == 0) return 0.0;

            double sum = 0.0;
            int count = 0;

            foreach (var idx in neighborVoxels)
            {
                if (Math.Abs(orderedKeys[idx].Item1 - ix) > 1) continue;
                if (Math.Abs(orderedKeys[idx].Item2 - iy) > 1) continue;
                sum += rawScore[idx];
                count++;
            }

            return count > 0 ? sum / count : rawScore[neighborVoxels[0]];
        }

        // -- Centroid of a voxel index list ------------------------------------
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

            return new Point3d(
                cx / indices.Count,
                cy / indices.Count,
                cz / indices.Count);
        }
    }
}

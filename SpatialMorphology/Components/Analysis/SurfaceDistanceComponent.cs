// -*- coding: utf-8 -*-
// Version 3.1.0
using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace SpatialMorphology
{
    /// <summary>
    /// Distance from the surface shell, in hops (topological) or model units
    /// (metric). Replaces the former SA_Depth and SA_Proximity components.
    /// </summary>
    /// <remarks>
    /// MERGED in 3.0.0 — was SA_Depth (mode 0) + SA_Proximity (mode 1).
    ///
    /// RENAMED in 3.1.0 — mode 1 is now called "Metric" rather than
    /// "Proximity", and its default channel label changed from "proximity" to
    /// "surface_distance". This frees the name Proximity for the component
    /// formerly called SA_GeometryDistance, which measures distance to
    /// arbitrary context geometry rather than to the voxel shell.
    ///
    /// Any saved ValueSet matrix holding a multiplier under the key
    /// "proximity" will not find it under "surface_distance". Re-open the
    /// ValueSet editor and confirm the column, or type "proximity" into this
    /// component's label input to keep the old key.
    ///
    /// The ComponentGuid is SA_Depth's, unchanged, so saved definitions that
    /// referenced SA_Depth still resolve to this class. SA_Proximity's GUID
    /// (A2B3C4D5-E6F7-8901-ABCD-012345678906) is retired — definitions using
    /// it will show an unrecognised object and must be rewired to this
    /// component with mode = 1.
    ///
    /// Both readouts come from ONE breadth-first traversal outward from
    /// SurfaceKeys:
    ///
    ///   hops         incremented once per BFS layer. Integer, unitless,
    ///                independent of VoxelSize.
    ///
    ///   metric       straight-line distance from the voxel centre back to
    ///                the centre of the surface voxel that seeded its branch.
    ///                Carried forward via nearestSurf, so it is NOT simply
    ///                hops * VoxelSize — around a re-entrant corner the
    ///                straight line is shorter than the path walked, and the
    ///                two modes diverge. In convex regions they stay
    ///                proportional.
    ///
    /// Do not feed both modes into one AnalysisStack. They measure the same
    /// spatial property and would double-count it.
    /// </remarks>
    public class SurfaceDistanceComponent : SAComponentBase
    {
        // ── Constructor ───────────────────────────────────────────────────────
        public SurfaceDistanceComponent()
            : base(
                "Surface Distance",
                "SurfDist",
                "Distance from the surface shell, measured outward by BFS.\n\n" +
                "Mode 0 — Depth (hops):\n" +
                "  0 = surface layer\n" +
                "  N = N voxel steps from the nearest surface voxel\n" +
                "  Integer and unitless. Does not change with voxel size.\n" +
                "  Use for discrete peel layers — 'second ring in'.\n\n" +
                "Mode 1 — Metric (model units):\n" +
                "  0.0 = voxel is on the surface shell\n" +
                "  N   = N model units from the originating surface centre\n" +
                "  Scales with voxel size. Use when the threshold is\n" +
                "  dimensional — daylight penetration, distance to facade.\n\n" +
                "The two modes are proportional in convex regions but diverge\n" +
                "around concavities, because Metric measures a straight line\n" +
                "back to the seed voxel rather than the path length walked.\n\n" +
                "This component measures distance to the voxel SHELL. For\n" +
                "distance to arbitrary context geometry, use Proximity.\n\n" +
                "invert = False (default): surface = low value, interior = high\n" +
                "invert = True: surface = high value, interior = low\n\n" +
                "Normalization is handled downstream by AnalysisStack.\n\n" +
                "Version 3.1.0",
                "Spatial Morphology",
                "2 | Analysis")
        { }

        // ── Ribbon placement ──────────────────────────────────────────────────
        // Voxel-internal analysis — no context geometry input.
        public override GH_Exposure Exposure => GH_Exposure.secondary;

        // ── GUID — DO NOT CHANGE ──────────────────────────────────────────────
        // SA_Depth's original GUID, kept so its saved definitions keep working.
        public override Guid ComponentGuid =>
            new Guid("F1A2B3C4-D5E6-7890-FABC-012345678905");

        // ── Icon ──────────────────────────────────────────────────────────────
        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "SpatialMorphology.Resources.SurfaceDistance_24.png");
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
                "Measurement mode:\n" +
                "  0 = Depth  (BFS hops, integer, unitless)\n" +
                "  1 = Metric (world-unit distance, scales with voxel size)",
                GH_ParamAccess.item, 0);
            pManager.AddBooleanParameter("invert", "I",
                "If True, reverse the values so surface voxels score high and\n" +
                "interior voxels score low.\n" +
                "Default: false.",
                GH_ParamAccess.item, false);
            pManager.AddTextParameter("label", "L",
                "Channel name used by AnalysisStack.\n" +
                "Leave blank to name it after the mode — 'depth' or\n" +
                "'surface_distance'.",
                GH_ParamAccess.item, "");
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("analysis", "A",
                "SpatialAnalysis object. Wire into AnalysisStack.",
                GH_ParamAccess.item);
            pManager.AddNumberParameter("values", "V",
                "Per-voxel distance in the selected mode, inverted if Invert is true.",
                GH_ParamAccess.list);
            pManager.AddPointParameter("centers", "C",
                "Voxel centres for preview.",
                GH_ParamAccess.list);
            pManager.AddColourParameter("gradient", "G",
                "Per-voxel gradient color from low (red) to high (blue).",
                GH_ParamAccess.list);
            pManager.AddTextParameter("info", "I",
                "Summary.",
                GH_ParamAccess.item);
        }

        // ── Solve ─────────────────────────────────────────────────────────────
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            object voxelGridObj = null;
            int mode = 0;
            bool invert = false;
            string label = "";

            if (!DA.GetData(0, ref voxelGridObj)) return;
            DA.GetData(1, ref mode);
            DA.GetData(2, ref invert);
            DA.GetData(3, ref label);

            var voxelGrid = UnwrapVoxelGrid(voxelGridObj);
            if (voxelGrid == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Could not read VoxelGrid object.");
                return;
            }

            mode = Math.Max(0, Math.Min(1, mode));

            // Blank label resolves to the mode name, so switching mode does not
            // silently keep a channel key that describes the other measurement.
            string defaultLabel = mode == 0 ? "depth" : "surface_distance";
            string resolvedLabel = string.IsNullOrWhiteSpace(label)
                ? defaultLabel : label.Trim();

            var surfKeys = voxelGrid.SurfaceKeys;
            var filled = voxelGrid.FilledKeysSet;

            if (surfKeys.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "No surface voxels found — mesh may not be closed.");
                return;
            }

            // ── One BFS, both readouts ────────────────────────────────────────
            var hopMap = new Dictionary<(int, int, int), int>();
            var metricMap = new Dictionary<(int, int, int), double>();
            var nearestSurf = new Dictionary<(int, int, int), Point3d>();
            var queue = new Queue<(int, int, int)>();

            foreach (var key in surfKeys)
            {
                var surfCenter = voxelGrid.KeyToCenter(key);
                hopMap[key] = 0;
                metricMap[key] = 0.0;
                nearestSurf[key] = surfCenter;
                queue.Enqueue(key);
            }

            int[] dx = { 1, -1, 0, 0, 0, 0 };
            int[] dy = { 0, 0, 1, -1, 0, 0 };
            int[] dz = { 0, 0, 0, 0, 1, -1 };

            while (queue.Count > 0)
            {
                var key = queue.Dequeue();
                int hops = hopMap[key];
                var seedCenter = nearestSurf[key];

                for (int i = 0; i < 6; i++)
                {
                    var nb = (key.Item1 + dx[i],
                              key.Item2 + dy[i],
                              key.Item3 + dz[i]);

                    if (!filled.Contains(nb) || hopMap.ContainsKey(nb)) continue;

                    // Hops advance one per layer. The metric is measured back to
                    // the ORIGINATING surface voxel, not to the parent voxel, so
                    // it stays a straight line rather than a walked path length.
                    var nbCenter = voxelGrid.KeyToCenter(nb);
                    hopMap[nb] = hops + 1;
                    metricMap[nb] = nbCenter.DistanceTo(seedCenter);
                    nearestSurf[nb] = seedCenter;
                    queue.Enqueue(nb);
                }
            }

            // ── Disconnected voxel guard ──────────────────────────────────────
            // Islands unreachable from the shell get one step past the maximum,
            // so they read as "deepest" rather than as surface.
            int hopMax = 0;
            foreach (var value in hopMap.Values)
                if (value > hopMax) hopMax = value;

            double metricMax = 0.0;
            foreach (var value in metricMap.Values)
                if (value > metricMax) metricMax = value;

            int nDisconnected = 0;
            foreach (var key in filled)
            {
                if (hopMap.ContainsKey(key)) continue;
                hopMap[key] = hopMax + 1;
                metricMap[key] = metricMax + voxelGrid.VoxelSize;
                nDisconnected++;
            }

            // ── Select the requested readout ──────────────────────────────────
            var orderedKeys = voxelGrid.FilledKeys;
            var raw = new List<double>(orderedKeys.Count);

            foreach (var key in orderedKeys)
            {
                if (mode == 0)
                    raw.Add(hopMap.ContainsKey(key) ? hopMap[key] : 0.0);
                else
                    raw.Add(metricMap.ContainsKey(key) ? metricMap[key] : 0.0);
            }

            // ── Invert if requested, then keep values/colors/preview in sync ──
            var outputValues = invert ? InvertValues(raw) : raw;
            var analysis = new SpatialAnalysis(resolvedLabel, outputValues);

            var centers = new List<Point3d>(orderedKeys.Count);
            foreach (var key in orderedKeys)
                centers.Add(voxelGrid.KeyToCenter(key));

            var gradient = ComputeGradient(outputValues);
            BuildPreviewData(voxelGrid, outputValues);

            // ── Stats ─────────────────────────────────────────────────────────
            double rawMin = raw.Count > 0 ? raw[0] : 0.0;
            double rawMax = rawMin;
            foreach (var value in raw)
            {
                if (value < rawMin) rawMin = value;
                if (value > rawMax) rawMax = value;
            }

            double outputMin = outputValues.Count > 0 ? outputValues[0] : 0.0;
            double outputMax = outputMin;
            foreach (var value in outputValues)
            {
                if (value < outputMin) outputMin = value;
                if (value > outputMax) outputMax = value;
            }

            string modeName = mode == 0 ? "Depth (hops)" : "Metric (model units)";
            string unitNote = mode == 0 ? "hops" : "world units";
            string format = mode == 0 ? "F0" : "F4";

            string info = string.Format(
                "Surface Distance | label='{0}' | mode={1} ({2}) | voxels={3} | invert={4}\n" +
                "raw=[{5} to {6}] | output=[{7} to {8}] ({9}, not normalized)\n" +
                "bfs_layers={10} | surface_voxels={11}{12}",
                resolvedLabel, mode, modeName, raw.Count, invert,
                rawMin.ToString(format), rawMax.ToString(format),
                outputMin.ToString(format), outputMax.ToString(format),
                unitNote,
                hopMax, surfKeys.Count,
                nDisconnected > 0
                    ? string.Format("\nWARNING: {0} disconnected voxels set past the maximum",
                        nDisconnected)
                    : "");

            DA.SetData(0, analysis);
            DA.SetDataList(1, outputValues);
            DA.SetDataList(2, centers);
            DA.SetDataList(3, gradient);
            DA.SetData(4, info);
        }
    }
}

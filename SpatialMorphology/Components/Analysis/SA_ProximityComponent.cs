// -*- coding: utf-8 -*-
// Version 2.0.0
using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace SpatialMorphology
{
    public class SA_ProximityComponent : SAComponentBase
    {
        // ── Constructor ───────────────────────────────────────────────────────
        public SA_ProximityComponent()
            : base(
                "SA_Proximity",
                "SA_Prox",
                "BFS distance from each voxel to the nearest surface voxel,\n" +
                "output as raw world-unit distance.\n\n" +
                "  0.0 = voxel is on the surface shell\n" +
                "  N   = N model units from the nearest surface voxel centre\n\n" +
                "Normalization is handled downstream by AnalysisStack.\n\n" +
                "Version 2.0.0",
                "Spatial Morphology",
                "Analysis")
        { }

        // ── GUID ──────────────────────────────────────────────────────────────
        public override Guid ComponentGuid =>
            new Guid("A2B3C4D5-E6F7-8901-ABCD-012345678906");

        // ── Icon ──────────────────────────────────────────────────────────────
        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "SpatialMorphology.Resources.SA_Proximity_24.png");
                return stream != null ? new Bitmap(stream) : null;
            }
        }

        // ── Parameters ────────────────────────────────────────────────────────
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("voxel_grid", "VG",
                "VoxelGrid object from the VoxelGrid component.",
                GH_ParamAccess.item);
            pManager.AddTextParameter("label", "L",
                "Channel name used by AnalysisStack. Default: 'proximity'.",
                GH_ParamAccess.item, "proximity");
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("analysis", "A",
                "SpatialAnalysis object. Wire into AnalysisStack.",
                GH_ParamAccess.item);
            pManager.AddNumberParameter("values", "V",
                "Per-voxel raw world-unit distances.",
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
            // ── Collect inputs ────────────────────────────────────────────────
            object voxelGridObj = null;
            string label = "proximity";

            if (!DA.GetData(0, ref voxelGridObj)) return;
            DA.GetData(1, ref label);

            // ── Unwrap VoxelGrid ──────────────────────────────────────────────
            var voxelGrid = UnwrapVoxelGrid(voxelGridObj);
            if (voxelGrid == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Could not read VoxelGrid object.");
                return;
            }

            // ── Default label ─────────────────────────────────────────────────
            string resolvedLabel = string.IsNullOrWhiteSpace(label)
                ? "proximity" : label.Trim();

            // ── Multi-source BFS from all surface voxels ──────────────────────
            var surfKeys = voxelGrid.SurfaceKeys;
            var filled = voxelGrid.FilledKeysSet;
            var distMap = new Dictionary<(int, int, int), double>();
            var nearestSurf = new Dictionary<(int, int, int), Point3d>();
            var queue = new Queue<(int, int, int)>();

            if (surfKeys.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "No surface voxels found — mesh may not be closed.");
                return;
            }

            foreach (var k in surfKeys)
            {
                var sc = voxelGrid.KeyToCenter(k);
                distMap[k] = 0.0;
                nearestSurf[k] = sc;
                queue.Enqueue(k);
            }

            int[] dx = { 1, -1, 0, 0, 0, 0 };
            int[] dy = { 0, 0, 1, -1, 0, 0 };
            int[] dz = { 0, 0, 0, 0, 1, -1 };

            while (queue.Count > 0)
            {
                var key = queue.Dequeue();
                var srcCenter = nearestSurf[key];

                for (int i = 0; i < 6; i++)
                {
                    var nb = (key.Item1 + dx[i],
                              key.Item2 + dy[i],
                              key.Item3 + dz[i]);

                    if (!filled.Contains(nb) || distMap.ContainsKey(nb))
                        continue;

                    var nbCenter = voxelGrid.KeyToCenter(nb);
                    double d = nbCenter.DistanceTo(srcCenter);
                    distMap[nb] = d;
                    nearestSurf[nb] = srcCenter;
                    queue.Enqueue(nb);
                }
            }

            // ── Disconnected voxel guard ──────────────────────────────────────
            double dMax = 0.0;
            int nDisconnected = 0;

            foreach (var v in distMap.Values)
                if (v > dMax) dMax = v;

            foreach (var k in filled)
            {
                if (!distMap.ContainsKey(k))
                {
                    distMap[k] = dMax + voxelGrid.VoxelSize;
                    nDisconnected++;
                }
            }

            // ── Build outputs ─────────────────────────────────────────────────
            var orderedKeys = voxelGrid.FilledKeys;
            var raw = new List<double>();

            foreach (var key in orderedKeys)
                raw.Add(distMap.ContainsKey(key) ? distMap[key] : 0.0);

            var analysis = new SpatialAnalysis(resolvedLabel, raw);

            var centers = new List<Point3d>();
            foreach (var key in orderedKeys)
                centers.Add(voxelGrid.KeyToCenter(key));

            var gradient = ComputeGradient(raw);

            // ── Build preview ─────────────────────────────────────────────────
            BuildPreviewData(voxelGrid, raw);

            // ── Stats ─────────────────────────────────────────────────────────
            double rawMin = raw.Count > 0 ? raw[0] : 0;
            double rawMax = raw.Count > 0 ? raw[0] : 0;

            foreach (var v in raw)
            {
                if (v < rawMin) rawMin = v;
                if (v > rawMax) rawMax = v;
            }

            string info = string.Format(
                "SA_Proximity | label='{0}' | voxels={1} | " +
                "raw_min={2:F4} raw_max={3:F4} (world units, not normalized){4}",
                resolvedLabel, raw.Count, rawMin, rawMax,
                nDisconnected > 0
                    ? string.Format(
                        " | WARNING: {0} disconnected voxels", nDisconnected)
                    : "");

            // ── Output ────────────────────────────────────────────────────────
            DA.SetData(0, analysis);
            DA.SetDataList(1, raw);
            DA.SetDataList(2, centers);
            DA.SetDataList(3, gradient);
            DA.SetData(4, info);
        }
    }
}
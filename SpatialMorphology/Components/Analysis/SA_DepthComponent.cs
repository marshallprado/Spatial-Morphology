// -*- coding: utf-8 -*-
// Version 2.0.0
using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace SpatialMorphology
{
    public class SA_DepthComponent : SAComponentBase
    {
        // ── Constructor ───────────────────────────────────────────────────────
        public SA_DepthComponent()
            : base(
                "SA_Depth",
                "SA_Dep",
                "BFS topological peel depth from the surface shell, output as raw integer depth.\n\n" +
                "  0 = surface layer\n" +
                "  N = N hops from the nearest surface voxel\n\n" +
                "Normalization is handled downstream by AnalysisStack.\n\n" +
                "Version 2.0.0",
                "Spatial Morphology",
                "Analysis")
        { }

        // ── GUID ──────────────────────────────────────────────────────────────
        public override Guid ComponentGuid =>
            new Guid("F1A2B3C4-D5E6-7890-FABC-012345678905");

        // ── Icon ──────────────────────────────────────────────────────────────
        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "SpatialMorphology.Resources.SA_Depth_24.png");
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
                "Channel name used by AnalysisStack. Default: 'depth'.",
                GH_ParamAccess.item, "depth");
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("analysis", "A",
                "SpatialAnalysis object. Wire into AnalysisStack.",
                GH_ParamAccess.item);
            pManager.AddNumberParameter("values", "V",
                "Per-voxel raw BFS depth (0 = surface).",
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
            string label = "depth";

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
                ? "depth" : label.Trim();

            // ── BFS from surface shell ────────────────────────────────────────
            var surfKeys = voxelGrid.SurfaceKeys;
            var filled = voxelGrid.FilledKeysSet;
            var depthMap = new Dictionary<(int, int, int), int>();
            var queue = new Queue<(int, int, int)>();

            foreach (var k in surfKeys)
            {
                depthMap[k] = 0;
                queue.Enqueue(k);
            }

            int[] dx = { 1, -1, 0, 0, 0, 0 };
            int[] dy = { 0, 0, 1, -1, 0, 0 };
            int[] dz = { 0, 0, 0, 0, 1, -1 };

            while (queue.Count > 0)
            {
                var key = queue.Dequeue();
                int d = depthMap[key];

                for (int i = 0; i < 6; i++)
                {
                    var nb = (key.Item1 + dx[i],
                              key.Item2 + dy[i],
                              key.Item3 + dz[i]);

                    if (filled.Contains(nb) && !depthMap.ContainsKey(nb))
                    {
                        depthMap[nb] = d + 1;
                        queue.Enqueue(nb);
                    }
                }
            }

            // ── Disconnected voxel guard ──────────────────────────────────────
            int bfsMax = 0;
            int nDisconnected = 0;

            foreach (var v in depthMap.Values)
                if (v > bfsMax) bfsMax = v;

            foreach (var k in filled)
            {
                if (!depthMap.ContainsKey(k))
                {
                    depthMap[k] = bfsMax + 1;
                    nDisconnected++;
                }
            }

            // ── Build outputs ─────────────────────────────────────────────────
            var orderedKeys = voxelGrid.FilledKeys;
            var raw = new List<double>();

            foreach (var key in orderedKeys)
                raw.Add(depthMap.ContainsKey(key) ? depthMap[key] : 0);

            var analysis = new SpatialAnalysis(resolvedLabel, raw);

            var centers = new List<Point3d>();
            foreach (var key in orderedKeys)
                centers.Add(voxelGrid.KeyToCenter(key));

            var gradient = ComputeGradient(raw);

            // ── Build preview ─────────────────────────────────────────────────
            BuildPreviewData(voxelGrid, raw);

            // ── Info ──────────────────────────────────────────────────────────
            string info = string.Format(
                "SA_Depth | label='{0}' | voxels={1} | bfs_layers={2} | " +
                "raw_min={3} raw_max={4} (hops, not normalized){5}",
                resolvedLabel, raw.Count, bfsMax,
                0, bfsMax,
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
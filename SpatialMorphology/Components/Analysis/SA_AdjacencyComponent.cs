// -*- coding: utf-8 -*-
// Version 2.0.0
using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace SpatialMorphology
{
    public class SA_AdjacencyComponent : SAComponentBase
    {
        // ── Constructor ───────────────────────────────────────────────────────
        public SA_AdjacencyComponent()
            : base(
                "SA_Adjacency",
                "SA_Adj",
                "Computes per-voxel face-neighbour adjacency count as a raw integer (0-6).\n\n" +
                "  0 = voxel has no filled neighbours (isolated / surface)\n" +
                "  6 = all 6 face-neighbours are filled (fully interior)\n\n" +
                "Normalization is handled downstream by AnalysisStack.\n\n" +
                "Version 2.0.0",
                "Spatial Morphology",
                "Analysis")
        { }

        // ── GUID ──────────────────────────────────────────────────────────────
        public override Guid ComponentGuid =>
            new Guid("E1F2A3B4-C5D6-7890-EFAB-012345678904");

        // ── Icon ──────────────────────────────────────────────────────────────
        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "SpatialMorphology.Resources.SA_Adjacency_24.png");
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
                "Channel name used by AnalysisStack. Default: 'adjacency'.",
                GH_ParamAccess.item, "adjacency");
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("analysis", "A",
                "SpatialAnalysis object. Wire into AnalysisStack.",
                GH_ParamAccess.item);
            pManager.AddNumberParameter("values", "V",
                "Per-voxel raw adjacency count (0-6).",
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
            string label = "adjacency";

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
                ? "adjacency" : label.Trim();

            // ── Compute raw adjacency counts (0-6) ────────────────────────────
            var orderedKeys = voxelGrid.FilledKeys;
            var raw = new List<double>();

            foreach (var key in orderedKeys)
                raw.Add(voxelGrid.AdjacencyCount(key));

            // ── Build outputs ─────────────────────────────────────────────────
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
                "SA_Adjacency | label='{0}' | voxels={1} | " +
                "raw_min={2} raw_max={3} (counts, not normalized)",
                resolvedLabel, raw.Count, (int)rawMin, (int)rawMax);

            // ── Output ────────────────────────────────────────────────────────
            DA.SetData(0, analysis);
            DA.SetDataList(1, raw);
            DA.SetDataList(2, centers);
            DA.SetDataList(3, gradient);
            DA.SetData(4, info);
        }
    }
}
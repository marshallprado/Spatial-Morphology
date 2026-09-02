// -*- coding: utf-8 -*-
// Version 3.0.0
using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace SpatialMorphology
{
    public class SA_ViewAnalysisComponent : SAComponentBase
    {
        // ── Constructor ───────────────────────────────────────────────────────
        public SA_ViewAnalysisComponent()
            : base(
                "SA_ViewAnalysis",
                "SA_View",
                "Computes per-voxel visibility toward view target points,\n" +
                "checking for obstructions against a context mesh.\n\n" +
                "For each voxel, rays are cast toward each view point.\n" +
                "A ray is blocked if it hits the obstruction mesh before\n" +
                "reaching the target point.\n\n" +
                "Raw output = count of visible target points.\n" +
                "Normalization is handled downstream by AnalysisStack.\n\n" +
                "Version 3.0.0",
                "Spatial Morphology",
                "Analysis")
        { }

        // ── GUID ──────────────────────────────────────────────────────────────
        public override Guid ComponentGuid =>
            new Guid("E5F6A7B8-C9D0-1234-EFAB-012345678912");

        // ── Icon ──────────────────────────────────────────────────────────────
        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "SpatialMorphology.Resources.SA_ViewAnalysis_24.png");
                return stream != null ? new Bitmap(stream) : null;
            }
        }

        // ── Parameters ────────────────────────────────────────────────────────
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("voxel_grid", "VG",
                "VoxelGrid object from the VoxelGrid component.",
                GH_ParamAccess.item);
            pManager.AddPointParameter("view_points", "VP",
                "Target points to measure visibility toward.\n" +
                "Each voxel scores the number of points it can see.",
                GH_ParamAccess.list);
            pManager.AddMeshParameter("obstruction_mesh", "O",
                "Mesh representing obstructions (e.g. urban context).\n" +
                "Rays that hit this mesh before reaching the target are blocked.",
                GH_ParamAccess.list);
            pManager.AddTextParameter("label", "L",
                "Channel name used by AnalysisStack. Default: 'view'.",
                GH_ParamAccess.item, "view");

            pManager[2].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("analysis", "A",
                "SpatialAnalysis object. Wire into AnalysisStack.",
                GH_ParamAccess.item);
            pManager.AddNumberParameter("values", "V",
                "Per-voxel raw visible target point count.",
                GH_ParamAccess.list);
            pManager.AddPointParameter("centers", "C",
                "Voxel centres for preview.",
                GH_ParamAccess.list);
            pManager.AddColourParameter("gradient", "G",
                "Per-voxel gradient color from low (red) to high (blue).\n" +
                "Use with Custom Preview component for visualization.",
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
            var viewPoints = new List<Point3d>();
            var obstructionMeshes = new List<Mesh>();
            string label = "view";

            if (!DA.GetData(0, ref voxelGridObj)) return;
            if (!DA.GetDataList(1, viewPoints)) return;
            DA.GetDataList(2, obstructionMeshes);
            DA.GetData(3, ref label);

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
                ? "view" : label.Trim();

            if (viewPoints.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Connect at least one view point.");
                return;
            }

            // ── Build combined obstruction mesh ───────────────────────────────
            Mesh obstructionMesh = null;
            if (obstructionMeshes.Count > 0)
            {
                obstructionMesh = new Mesh();
                foreach (var m in obstructionMeshes)
                    if (m != null) obstructionMesh.Append(m);
                obstructionMesh.Compact();
            }

            // ── Compute view analysis ─────────────────────────────────────────
            var orderedKeys = voxelGrid.FilledKeys;
            var raw = new List<double>();
            int nTargets = viewPoints.Count;
            double nudge = voxelGrid.VoxelSize * 1e-3;

            foreach (var key in orderedKeys)
            {
                Point3d origin = voxelGrid.KeyToCenter(key);
                int visible = 0;

                foreach (var target in viewPoints)
                {
                    Vector3d dir = target - origin;
                    double length = dir.Length;

                    if (length < nudge)
                    {
                        visible++;
                        continue;
                    }

                    dir.Unitize();

                    bool blocked = false;

                    if (obstructionMesh != null)
                    {
                        var ray = new Ray3d(origin, dir);
                        double t = Intersection.MeshRay(obstructionMesh, ray);

                        if (t >= nudge && t < length - nudge)
                            blocked = true;
                    }

                    if (!blocked)
                        visible++;
                }

                raw.Add((double)visible);
            }

            // ── Build SpatialAnalysis ─────────────────────────────────────────
            var analysis = new SpatialAnalysis(resolvedLabel, raw);

            // ── Build centers ─────────────────────────────────────────────────
            var centers = new List<Point3d>();
            foreach (var key in orderedKeys)
                centers.Add(voxelGrid.KeyToCenter(key));

            // ── Build gradient ────────────────────────────────────────────────
            var gradient = ComputeGradient(raw);

            // ── Build preview data ────────────────────────────────────────────
            BuildPreviewData(voxelGrid, raw);

            // ── Stats ─────────────────────────────────────────────────────────
            double rawMin = raw.Count > 0 ? raw[0] : 0;
            double rawMax = raw.Count > 0 ? raw[0] : 0;
            double rawSum = 0;

            foreach (var v in raw)
            {
                if (v < rawMin) rawMin = v;
                if (v > rawMax) rawMax = v;
                rawSum += v;
            }

            double rawMean = raw.Count > 0 ? rawSum / raw.Count : 0;

            string info = string.Format(
                "SA_ViewAnalysis | label='{0}' | voxels={1} | " +
                "view_points={2} | obstructions={3} | " +
                "raw_min={4:F0} raw_max={5:F0} raw_mean={6:F1} " +
                "(visible point count, not normalized)",
                resolvedLabel, raw.Count,
                nTargets,
                obstructionMesh != null
                    ? obstructionMeshes.Count.ToString()
                    : "none",
                rawMin, rawMax, rawMean);

            // ── Output ────────────────────────────────────────────────────────
            DA.SetData(0, analysis);
            DA.SetDataList(1, raw);
            DA.SetDataList(2, centers);
            DA.SetDataList(3, gradient);
            DA.SetData(4, info);
        }
    }
}
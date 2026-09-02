// -*- coding: utf-8 -*-
// Version 1.0.0
using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace SpatialMorphology
{
    public class SA_SolarComponent : SAComponentBase
    {
        // ── Constructor ───────────────────────────────────────────────────────
        public SA_SolarComponent()
            : base(
                "SA_Solar",
                "SA_Sol",
                "Computes per-voxel solar exposure by casting rays toward\n" +
                "each sun vector and checking for obstructions.\n\n" +
                "Raw output = count of unobstructed sun vectors.\n\n" +
                "High value = high solar exposure.\n" +
                "Low value  = shaded / obstructed.\n\n" +
                "Voxels below the construction plane are set to 0.\n\n" +
                "Compatible with Ladybug sun vectors or the native\n" +
                "SunVectors component.\n\n" +
                "Normalization is handled downstream by AnalysisStack.\n\n" +
                "Version 1.0.0",
                "Spatial Morphology",
                "Analysis")
        { }

        // ── GUID ──────────────────────────────────────────────────────────────
        public override Guid ComponentGuid =>
            new Guid("A8B9C0D1-E2F3-4567-ABCD-012345678915");

        // ── Icon ──────────────────────────────────────────────────────────────
        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "SpatialMorphology.Resources.SA_Solar_24.png");
                return stream != null ? new Bitmap(stream) : null;
            }
        }

        // ── Parameters ────────────────────────────────────────────────────────
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("voxel_grid", "VG",
                "VoxelGrid object from the VoxelGrid component.",
                GH_ParamAccess.item);
            pManager.AddVectorParameter("sun_vectors", "SV",
                "Unit vectors pointing from ground toward sun.\n" +
                "From SunVectors component or Ladybug SunPath.\n" +
                "Only above-horizon vectors should be included.",
                GH_ParamAccess.list);
            pManager.AddMeshParameter("obstacles", "O",
                "Obstacle meshes that block sunlight.\n" +
                "Typically the urban context or building envelope.",
                GH_ParamAccess.list);
            pManager.AddTextParameter("label", "L",
                "Channel name used by AnalysisStack. Default: 'solar'.",
                GH_ParamAccess.item, "solar");

            pManager[2].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("analysis", "A",
                "SpatialAnalysis object. Wire into AnalysisStack.",
                GH_ParamAccess.item);
            pManager.AddNumberParameter("values", "V",
                "Per-voxel count of unobstructed sun vectors.\n" +
                "0 = fully shaded or below construction plane.\n" +
                "Max = total sun vector count (fully exposed).",
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
            var sunVectors = new List<Vector3d>();
            var obstacleMeshes = new List<Mesh>();
            string label = "solar";

            if (!DA.GetData(0, ref voxelGridObj)) return;
            if (!DA.GetDataList(1, sunVectors)) return;
            DA.GetDataList(2, obstacleMeshes);
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
                ? "solar" : label.Trim();

            if (sunVectors.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Connect at least one sun vector.\n" +
                    "Use the SunVectors component or Ladybug SunPath.");
                return;
            }

            // ── Unitize all sun vectors ───────────────────────────────────────
            var unitVectors = new List<Vector3d>(sunVectors.Count);
            foreach (var v in sunVectors)
            {
                var uv = new Vector3d(v);
                uv.Unitize();
                unitVectors.Add(uv);
            }

            // ── Build combined obstacle mesh ──────────────────────────────────
            Mesh obstacleMesh = null;
            if (obstacleMeshes.Count > 0)
            {
                obstacleMesh = new Mesh();
                foreach (var m in obstacleMeshes)
                    if (m != null) obstacleMesh.Append(m);
                obstacleMesh.Compact();
            }

            // ── Compute solar exposure per voxel ──────────────────────────────
            var orderedKeys = voxelGrid.FilledKeys;
            var raw = new List<double>();
            int nBelowGrade = 0;
            int nAboveGrade = 0;
            int nSunVectors = unitVectors.Count;
            double nudge = voxelGrid.VoxelSize * 1e-3;

            foreach (var key in orderedKeys)
            {
                // ── Skip below-grade voxels ───────────────────────────────────
                if (voxelGrid.IsBelowGrade(key))
                {
                    raw.Add(0.0);
                    nBelowGrade++;
                    continue;
                }

                nAboveGrade++;
                Point3d origin = voxelGrid.KeyToCenter(key);
                int exposed = 0;

                foreach (var sunDir in unitVectors)
                {
                    bool blocked = false;

                    if (obstacleMesh != null)
                    {
                        // Cast ray FROM voxel TOWARD sun
                        var ray = new Ray3d(origin, sunDir);
                        double t = Intersection.MeshRay(obstacleMesh, ray);

                        // Hit at t >= nudge means something is between
                        // voxel and sun
                        if (t >= nudge)
                            blocked = true;
                    }

                    if (!blocked)
                        exposed++;
                }

                raw.Add((double)exposed);
            }

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
            double rawSum = 0;

            foreach (var v in raw)
            {
                if (v < rawMin) rawMin = v;
                if (v > rawMax) rawMax = v;
                rawSum += v;
            }

            double rawMean = raw.Count > 0 ? rawSum / raw.Count : 0;
            double pctMax = nSunVectors > 0
                ? (rawMean / nSunVectors) * 100.0 : 0;

            string info = string.Format(
                "SA_Solar | label='{0}' | voxels={1}\n" +
                "sun_vectors={2} | above_grade={3} | below_grade={4} (value=0)\n" +
                "raw_min={5:F0} raw_max={6:F0} raw_mean={7:F1}\n" +
                "avg_exposure={8:F1}% of sun vectors unobstructed\n" +
                "(unobstructed sun vector count, not normalized)\n" +
                "obstacles={9}",
                resolvedLabel, raw.Count,
                nSunVectors, nAboveGrade, nBelowGrade,
                rawMin, rawMax, rawMean,
                pctMax,
                obstacleMesh != null
                    ? obstacleMeshes.Count + " mesh(es)"
                    : "none");

            // ── Output ────────────────────────────────────────────────────────
            DA.SetData(0, analysis);
            DA.SetDataList(1, raw);
            DA.SetDataList(2, centers);
            DA.SetDataList(3, gradient);
            DA.SetData(4, info);
        }
    }
}
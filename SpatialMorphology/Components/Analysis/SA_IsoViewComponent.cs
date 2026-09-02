// -*- coding: utf-8 -*-
// Version 2.3.0
using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace SpatialMorphology
{
    public class SA_IsovistComponent : SAComponentBase
    {
        // ── Constructor ───────────────────────────────────────────────────────
        public SA_IsovistComponent()
            : base(
                "SA_Isovist",
                "SA_Iso",
                "Computes a 3D or 2D isovist at each voxel centre.\n\n" +
                "Voxels below the VoxelGrid construction plane are set to 0\n" +
                "without computing rays — improves performance for models\n" +
                "with below-grade levels.\n\n" +
                "For each voxel above the construction plane, rays are cast\n" +
                "uniformly in all directions. Each ray travels until it\n" +
                "hits an obstacle or reaches the radius limit.\n\n" +
                "Output value = sum of all ray distances (clamped to radius).\n\n" +
                "High value = open space (large isovist volume).\n" +
                "Low value  = enclosed space (small isovist volume).\n\n" +
                "Mode 0 — Spherical (3D):\n" +
                "  Rays distributed uniformly across full sphere.\n" +
                "  Best for atria, vertical connections, sky exposure.\n\n" +
                "Mode 1 — Planar (2D):\n" +
                "  Rays distributed uniformly in horizontal plane only.\n" +
                "  Aligned to construction plane Z axis.\n" +
                "  Best for room connectivity, street visibility,\n" +
                "  urban space quality. ~16x fewer rays needed\n" +
                "  for equivalent angular resolution vs spherical.\n\n" +
                "Normalization is handled downstream by AnalysisStack.\n\n" +
                "Version 2.3.0",
                "Spatial Morphology",
                "Analysis")
        { }

        // ── GUID ──────────────────────────────────────────────────────────────
        public override Guid ComponentGuid =>
            new Guid("F6A7B8C9-D0E1-2345-FABC-012345678913");

        // ── Icon ──────────────────────────────────────────────────────────────
        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "SpatialMorphology.Resources.SA_Isovist_24.png");
                return stream != null ? new Bitmap(stream) : null;
            }
        }

        // ── Parameters ────────────────────────────────────────────────────────
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("voxel_grid", "VG",
                "VoxelGrid object from the VoxelGrid component.",
                GH_ParamAccess.item);
            pManager.AddMeshParameter("obstacles", "O",
                "Obstacle meshes that block the isovist rays.\n" +
                "Typically the building envelope or urban context.",
                GH_ParamAccess.list);
            pManager.AddIntegerParameter("sample_count", "S",
                "Number of rays to cast per voxel.\n" +
                "Mode 0 (Spherical): recommend 100-500.\n" +
                "Mode 1 (Planar):    recommend 36-72.\n" +
                "Default: 100.",
                GH_ParamAccess.item, 100);
            pManager.AddNumberParameter("radius", "R",
                "Maximum ray distance in model units.\n" +
                "Rays that do not hit an obstacle are clamped to this value.\n" +
                "Default: 1000.",
                GH_ParamAccess.item, 1000.0);
            pManager.AddIntegerParameter("mode", "M",
                "Ray distribution mode:\n" +
                "  0 = Spherical (3D) — full sphere, Fibonacci distribution\n" +
                "  1 = Planar (2D)    — horizontal plane, uniform angular spacing\n" +
                "Default: 1 (Planar — faster and more meaningful for most workflows).",
                GH_ParamAccess.item, 1);
            pManager.AddTextParameter("label", "L",
                "Channel name used by AnalysisStack. Default: 'isovist'.",
                GH_ParamAccess.item, "isovist");

            pManager[1].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("analysis", "A",
                "SpatialAnalysis object. Wire into AnalysisStack.",
                GH_ParamAccess.item);
            pManager.AddNumberParameter("values", "V",
                "Per-voxel sum of isovist ray distances (clamped to radius).\n" +
                "0 = below construction plane or fully enclosed.",
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
            var obstacleMeshes = new List<Mesh>();
            int sampleCount = 100;
            double radius = 1000.0;
            int mode = 1;
            string label = "isovist";

            if (!DA.GetData(0, ref voxelGridObj)) return;
            DA.GetDataList(1, obstacleMeshes);
            DA.GetData(2, ref sampleCount);
            DA.GetData(3, ref radius);
            DA.GetData(4, ref mode);
            DA.GetData(5, ref label);

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
                ? "isovist" : label.Trim();

            sampleCount = Math.Max(4, sampleCount);
            radius = Math.Max(1.0, radius);
            mode = Math.Max(0, Math.Min(1, mode));

            // ── Build combined obstacle mesh ──────────────────────────────────
            Mesh obstacleMesh = null;
            if (obstacleMeshes.Count > 0)
            {
                obstacleMesh = new Mesh();
                foreach (var m in obstacleMeshes)
                    if (m != null) obstacleMesh.Append(m);
                obstacleMesh.Compact();
            }

            // ── Extract construction plane Z axis for planar mode ─────────────
            var planeZAxis = new Vector3d(
                voxelGrid.PlaneToWorld.M02,
                voxelGrid.PlaneToWorld.M12,
                voxelGrid.PlaneToWorld.M22);
            planeZAxis.Unitize();

            // ── Generate ray directions ───────────────────────────────────────
            List<Vector3d> directions;
            if (mode == 0)
                directions = FibonacciSphere(sampleCount);
            else
                directions = PlanarCircle(sampleCount, planeZAxis);

            // ── Compute isovist per voxel ─────────────────────────────────────
            var orderedKeys = voxelGrid.FilledKeys;
            var raw = new List<double>();
            int nBelowPlane = 0;
            int nAbovePlane = 0;

            foreach (var key in orderedKeys)
            {
                // ── Check if voxel is below construction plane ────────────────
                // Uses pre-computed BelowGradeKeys from VoxelGrid — O(1) lookup
                if (voxelGrid.IsBelowGrade(key))
                {
                    raw.Add(0.0);
                    nBelowPlane++;
                    continue;
                }

                // ── Above construction plane — compute isovist ────────────────
                nAbovePlane++;
                Point3d origin = voxelGrid.KeyToCenter(key);
                double sum = 0.0;

                foreach (var dir in directions)
                {
                    double dist = radius;

                    if (obstacleMesh != null)
                    {
                        var ray = new Ray3d(origin, dir);
                        double t = Intersection.MeshRay(obstacleMesh, ray);

                        if (t >= 0)
                            dist = Math.Min(t, radius);
                    }

                    sum += dist;
                }

                raw.Add(sum);
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
            double maxPossible = radius * sampleCount;

            string[] modeNames = { "Spherical (3D)", "Planar (2D)" };

            string info = string.Format(
                "SA_Isovist | label='{0}' | mode={1} ({2}) | " +
                "voxels={3} | rays={4} | radius={5:F1}\n" +
                "above_plane={6} | below_plane={7} (value=0, skipped)\n" +
                "max_possible={8:F1} | " +
                "raw_min={9:F1} raw_max={10:F1} raw_mean={11:F1}\n" +
                "(sum of ray distances clamped to radius, not normalized)\n" +
                "obstacles={12}",
                resolvedLabel,
                mode, modeNames[mode],
                raw.Count,
                sampleCount, radius,
                nAbovePlane, nBelowPlane,
                maxPossible,
                rawMin, rawMax, rawMean,
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

        // ── Mode 0 — Fibonacci sphere (3D uniform distribution) ───────────────
        private List<Vector3d> FibonacciSphere(int count)
        {
            var dirs = new List<Vector3d>(count);
            double phi = Math.PI * (3.0 - Math.Sqrt(5.0));

            for (int i = 0; i < count; i++)
            {
                double y = 1.0 - (i / (double)(count - 1)) * 2.0;
                double rxy = Math.Sqrt(Math.Max(0.0, 1.0 - y * y));
                double theta = phi * i;
                double x = Math.Cos(theta) * rxy;
                double z = Math.Sin(theta) * rxy;

                var dir = new Vector3d(x, y, z);
                dir.Unitize();
                dirs.Add(dir);
            }

            return dirs;
        }

        // ── Mode 1 — Planar circle (2D uniform distribution) ──────────────────
        private List<Vector3d> PlanarCircle(int count, Vector3d zAxis)
        {
            var dirs = new List<Vector3d>(count);

            Vector3d xAxis = Vector3d.CrossProduct(zAxis, Vector3d.ZAxis);
            if (xAxis.Length < 1e-6)
                xAxis = Vector3d.CrossProduct(zAxis, Vector3d.XAxis);
            xAxis.Unitize();

            Vector3d yAxis = Vector3d.CrossProduct(zAxis, xAxis);
            yAxis.Unitize();

            double angleStep = 2.0 * Math.PI / count;

            for (int i = 0; i < count; i++)
            {
                double angle = i * angleStep;
                double cosA = Math.Cos(angle);
                double sinA = Math.Sin(angle);

                var dir = new Vector3d(
                    cosA * xAxis.X + sinA * yAxis.X,
                    cosA * xAxis.Y + sinA * yAxis.Y,
                    cosA * xAxis.Z + sinA * yAxis.Z);

                dir.Unitize();
                dirs.Add(dir);
            }

            return dirs;
        }
    }
}
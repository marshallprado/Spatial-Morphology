// -*- coding: utf-8 -*-
// Version 2.0.0
using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace SpatialMorphology
{
    public class SA_OrientationComponent : SAComponentBase
    {
        // ── Constructor ───────────────────────────────────────────────────────
        public SA_OrientationComponent()
            : base(
                "SA_Orientation",
                "SA_Orient",
                "Computes the dominant facing direction of each surface voxel.\n\n" +
                "For each surface voxel, counts the number of missing face neighbours\n" +
                "in each of the 6 axis-aligned directions. The dominant direction is\n" +
                "the one with the most open faces.\n\n" +
                "Output values:\n" +
                "  0 = +X (East)\n" +
                "  1 = -X (West)\n" +
                "  2 = +Y (North)\n" +
                "  3 = -Y (South)\n" +
                "  4 = +Z (Up)\n" +
                "  5 = -Z (Down)\n" +
                "  -1 = interior voxel (fully enclosed)\n\n" +
                "Normalization is handled downstream by AnalysisStack.\n\n" +
                "Version 2.0.0",
                "Spatial Morphology",
                "Analysis")
        { }

        // ── GUID ──────────────────────────────────────────────────────────────
        public override Guid ComponentGuid =>
            new Guid("D4E5F6A7-B8C9-0123-DEFA-012345678911");

        // ── Icon ──────────────────────────────────────────────────────────────
        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "SpatialMorphology.Resources.SA_Orientation_24.png");
                return stream != null ? new Bitmap(stream) : null;
            }
        }

        // ── Direction constants ───────────────────────────────────────────────
        private static readonly int[] DIR_DX = { 1, -1, 0, 0, 0, 0 };
        private static readonly int[] DIR_DY = { 0, 0, 1, -1, 0, 0 };
        private static readonly int[] DIR_DZ = { 0, 0, 0, 0, 1, -1 };
        private static readonly string[] DIR_NAMES =
        {
            "+X (East)", "-X (West)",
            "+Y (North)", "-Y (South)",
            "+Z (Up)", "-Z (Down)"
        };

        // ── Parameters ────────────────────────────────────────────────────────
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("voxel_grid", "VG",
                "VoxelGrid object from the VoxelGrid component.",
                GH_ParamAccess.item);
            pManager.AddTextParameter("label", "L",
                "Channel name used by AnalysisStack. Default: 'orientation'.",
                GH_ParamAccess.item, "orientation");
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("analysis", "A",
                "SpatialAnalysis object. Wire into AnalysisStack.",
                GH_ParamAccess.item);
            pManager.AddNumberParameter("values", "V",
                "Per-voxel dominant direction index (0-5). -1 = interior.",
                GH_ParamAccess.list);
            pManager.AddPointParameter("centers", "C",
                "Voxel centres for preview.",
                GH_ParamAccess.list);
            pManager.AddColourParameter("gradient", "G",
                "Per-voxel gradient color from low (red) to high (blue).",
                GH_ParamAccess.list);
            pManager.AddVectorParameter("direction_vectors", "D",
                "Per-voxel dominant direction as a unit vector.\n" +
                "Zero vector for interior voxels.",
                GH_ParamAccess.list);
            pManager.AddTextParameter("info", "I",
                "Summary including count per direction.",
                GH_ParamAccess.item);
        }

        // ── Solve ─────────────────────────────────────────────────────────────
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // ── Collect inputs ────────────────────────────────────────────────
            object voxelGridObj = null;
            string label = "orientation";

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
                ? "orientation" : label.Trim();

            // ── Unit vectors per direction ─────────────────────────────────────
            var unitVectors = new Vector3d[]
            {
                new Vector3d( 1,  0,  0),
                new Vector3d(-1,  0,  0),
                new Vector3d( 0,  1,  0),
                new Vector3d( 0, -1,  0),
                new Vector3d( 0,  0,  1),
                new Vector3d( 0,  0, -1),
            };

            // ── Compute orientation ───────────────────────────────────────────
            var orderedKeys = voxelGrid.FilledKeys;
            var filled = voxelGrid.FilledKeysSet;
            var raw = new List<double>();
            var dirVectors = new List<Vector3d>();
            var dirCounts = new int[6];

            foreach (var key in orderedKeys)
            {
                int ix = key.Item1;
                int iy = key.Item2;
                int iz = key.Item3;

                var openCounts = new int[6];
                int totalOpen = 0;

                for (int d = 0; d < 6; d++)
                {
                    var nb = (ix + DIR_DX[d],
                              iy + DIR_DY[d],
                              iz + DIR_DZ[d]);
                    if (!filled.Contains(nb))
                    {
                        openCounts[d]++;
                        totalOpen++;
                    }
                }

                // Interior voxel
                if (totalOpen == 0)
                {
                    raw.Add(-1.0);
                    dirVectors.Add(Vector3d.Zero);
                    continue;
                }

                // Find dominant direction
                int dominantDir = 0;
                int dominantCount = openCounts[0];

                for (int d = 1; d < 6; d++)
                {
                    if (openCounts[d] > dominantCount)
                    {
                        dominantCount = openCounts[d];
                        dominantDir = d;
                    }
                }

                raw.Add((double)dominantDir);
                dirVectors.Add(unitVectors[dominantDir]);
                dirCounts[dominantDir]++;
            }

            // ── Build outputs ─────────────────────────────────────────────────
            var analysis = new SpatialAnalysis(resolvedLabel, raw);

            var centers = new List<Point3d>();
            foreach (var key in orderedKeys)
                centers.Add(voxelGrid.KeyToCenter(key));

            var gradient = ComputeGradient(raw);

            // ── Build preview ─────────────────────────────────────────────────
            BuildPreviewData(voxelGrid, raw);

            // ── Info ──────────────────────────────────────────────────────────
            int nInterior = 0;
            foreach (var v in raw)
                if (v < 0) nInterior++;

            var infoLines = new System.Text.StringBuilder();
            infoLines.AppendLine(string.Format(
                "SA_Orientation | label='{0}' | voxels={1} | interior={2}",
                resolvedLabel, raw.Count, nInterior));
            infoLines.AppendLine("Direction counts:");
            for (int d = 0; d < 6; d++)
                infoLines.AppendLine(string.Format(
                    "  {0}: {1}", DIR_NAMES[d], dirCounts[d]));

            // ── Output ────────────────────────────────────────────────────────
            DA.SetData(0, analysis);
            DA.SetDataList(1, raw);
            DA.SetDataList(2, centers);
            DA.SetDataList(3, gradient);
            DA.SetDataList(4, dirVectors);
            DA.SetData(5, infoLines.ToString().TrimEnd());
        }
    }
}
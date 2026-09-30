// -*- coding: utf-8 -*-
// Version 2.0.0
using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace SpatialMorphology
{
    /// <summary>
    /// How many sun vectors reach each voxel centre unobstructed.
    /// </summary>
    /// <remarks>
    /// RENAMED in 2.0.0 — was SA_Solar / SA_Sol. The ComponentGuid is
    /// deliberately UNCHANGED so saved definitions still resolve to this class
    /// rather than appearing as unrecognised objects.
    ///
    /// The default label is unchanged at "solar", so no ValueSet matrix needs
    /// re-confirming after this rename.
    ///
    /// Voxels below the construction plane are set to 0 before inversion, not
    /// after. A below-grade voxel therefore reads 0 = "no sun" in the default
    /// orientation, and with invert = true it becomes the HIGHEST value along
    /// with the genuinely shaded voxels. That is intentional — below grade is
    /// shaded — but it means below-grade voxels are not distinguishable from
    /// deeply shaded above-grade ones in the output. Use Floor Level as a
    /// second channel if that distinction matters.
    /// </remarks>
    public class SolarComponent : SAComponentBase
    {
        // ── Constructor ───────────────────────────────────────────────────────
        public SolarComponent()
            : base(
                "Solar",
                "Solar",
                "How many sun vectors reach each voxel centre unobstructed.\n\n" +
                "A ray is cast from the voxel centre along every sun vector.\n" +
                "If it hits the obstacle mesh the sun is blocked at that hour.\n" +
                "The output is the COUNT of unobstructed vectors.\n\n" +
                "  high value = well exposed\n" +
                "  low value  = shaded\n\n" +
                "sun_vectors point FROM the ground TOWARD the sun. Supply them\n" +
                "from the SunVectors component or a Ladybug SunPath. Include\n" +
                "only above-horizon vectors — a below-horizon vector casts a\n" +
                "ray into the ground and reads as shade everywhere.\n\n" +
                "Voxels below the construction plane are set to 0 without\n" +
                "casting any rays.\n\n" +
                "invert = False (default): shaded = low value, exposed = high\n" +
                "invert = True: shaded = high value, exposed = low value\n" +
                "  Use when solar gain is undesirable — cold storage, a\n" +
                "  server room, a screening room, west-facing glazing in a\n" +
                "  hot climate.\n\n" +
                "Normalization is handled downstream by AnalysisStack.\n\n" +
                "Version 2.0.0",
                "Spatial Morphology",
                "2 | Analysis")
        { }

        // ── Ribbon placement ──────────────────────────────────────────────────
        // Contextual analysis — takes external sun vectors and obstacles.
        public override GH_Exposure Exposure => GH_Exposure.primary;

        // ── GUID — DO NOT CHANGE ──────────────────────────────────────────────
        // Kept from the SA_Solar era so old .gh files keep working.
        public override Guid ComponentGuid =>
            new Guid("A8B9C0D1-E2F3-4567-ABCD-012345678915");

        // ── Icon ──────────────────────────────────────────────────────────────
        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "SpatialMorphology.Resources.Solar_24.png");
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
                "From the SunVectors component or Ladybug SunPath.\n" +
                "Include only above-horizon vectors.",
                GH_ParamAccess.list);
            pManager.AddMeshParameter("obstacles", "O",
                "Meshes that block sunlight — urban context or building\n" +
                "envelope.\n" +
                "Optional — with none connected every voxel above the\n" +
                "construction plane scores the full sun vector count.",
                GH_ParamAccess.list);
            pManager.AddBooleanParameter("invert", "I",
                "If True, reverse the values so shaded voxels score high and\n" +
                "exposed voxels score low.\n" +
                "Default: false.",
                GH_ParamAccess.item, false);
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
                "Per-voxel count of unobstructed sun vectors, inverted if\n" +
                "Invert is true. 0 = below construction plane, or fully\n" +
                "shaded.",
                GH_ParamAccess.list);
            pManager.AddPointParameter("centers", "C",
                "Voxel centres for preview.",
                GH_ParamAccess.list);
            pManager.AddColourParameter("gradient", "G",
                "Per-voxel gradient color from low (red) to high (blue),\n" +
                "based on the output values.",
                GH_ParamAccess.list);
            pManager.AddTextParameter("info", "I",
                "Summary.",
                GH_ParamAccess.item);
        }

        // ── Solve ─────────────────────────────────────────────────────────────
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            object voxelGridObj = null;
            var sunVectors = new List<Vector3d>();
            var obstacleMeshes = new List<Mesh>();
            bool invert = false;
            string label = "solar";

            if (!DA.GetData(0, ref voxelGridObj)) return;
            if (!DA.GetDataList(1, sunVectors)) return;
            DA.GetDataList(2, obstacleMeshes);
            DA.GetData(3, ref invert);
            DA.GetData(4, ref label);

            var voxelGrid = UnwrapVoxelGrid(voxelGridObj);
            if (voxelGrid == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Could not read VoxelGrid object.");
                return;
            }

            string resolvedLabel = string.IsNullOrWhiteSpace(label)
                ? "solar" : label.Trim();

            if (sunVectors.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Connect at least one sun vector.\n" +
                    "Use the SunVectors component or Ladybug SunPath.");
                return;
            }

            // ── Unitize, and count any that point below the horizon ───────────
            var unitVectors = new List<Vector3d>(sunVectors.Count);
            int nBelowHorizon = 0;

            foreach (var v in sunVectors)
            {
                var uv = new Vector3d(v);
                uv.Unitize();
                if (uv.Z <= 0.0) nBelowHorizon++;
                unitVectors.Add(uv);
            }

            if (nBelowHorizon > 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    string.Format(
                        "{0} of {1} sun vectors point at or below the horizon.\n" +
                        "These cast rays into the ground and read as shade " +
                        "everywhere. Filter to above-horizon vectors only.",
                        nBelowHorizon, unitVectors.Count));
            }

            // ── Merge obstacles into one mesh for faster ray casting ──────────
            Mesh obstacleMesh = null;
            if (obstacleMeshes.Count > 0)
            {
                obstacleMesh = new Mesh();
                foreach (var m in obstacleMeshes)
                    if (m != null) obstacleMesh.Append(m);
                obstacleMesh.Compact();
            }
            else
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "No obstacles connected — every voxel above the\n" +
                    "construction plane scores the full sun vector count.");
            }

            // ── Count unobstructed sun vectors per voxel ──────────────────────
            var orderedKeys = voxelGrid.FilledKeys;
            var raw = new List<double>(orderedKeys.Count);
            int nBelowGrade = 0;
            int nAboveGrade = 0;
            int nSunVectors = unitVectors.Count;
            double nudge = voxelGrid.VoxelSize * 1e-3;

            foreach (var key in orderedKeys)
            {
                // Below grade is shaded by definition — skip the ray casting.
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
                        double hit = Intersection.MeshRay(
                            obstacleMesh, new Ray3d(origin, sunDir));
                        if (hit >= nudge) blocked = true;
                    }

                    if (!blocked) exposed++;
                }

                raw.Add(exposed);
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
            double rawSum = 0.0;
            foreach (var value in raw)
            {
                if (value < rawMin) rawMin = value;
                if (value > rawMax) rawMax = value;
                rawSum += value;
            }

            double outputMin = outputValues.Count > 0 ? outputValues[0] : 0.0;
            double outputMax = outputMin;
            foreach (var value in outputValues)
            {
                if (value < outputMin) outputMin = value;
                if (value > outputMax) outputMax = value;
            }

            double rawMean = raw.Count > 0 ? rawSum / raw.Count : 0.0;
            double pctMax = nSunVectors > 0
                ? rawMean / nSunVectors * 100.0 : 0.0;

            string info = string.Format(
                "Solar | label='{0}' | voxels={1} | invert={2}\n" +
                "sun_vectors={3}{4} | above_grade={5} | below_grade={6} (raw=0)\n" +
                "raw=[{7:F0} to {8:F0}] | output=[{9:F0} to {10:F0}]\n" +
                "avg_exposure={11:F1}% of sun vectors unobstructed\n" +
                "obstacles={12}",
                resolvedLabel, raw.Count, invert,
                nSunVectors,
                nBelowHorizon > 0
                    ? string.Format(" ({0} below horizon)", nBelowHorizon)
                    : "",
                nAboveGrade, nBelowGrade,
                rawMin, rawMax, outputMin, outputMax,
                pctMax,
                obstacleMesh != null
                    ? obstacleMeshes.Count + " mesh(es)"
                    : "none");

            DA.SetData(0, analysis);
            DA.SetDataList(1, outputValues);
            DA.SetDataList(2, centers);
            DA.SetDataList(3, gradient);
            DA.SetData(4, info);
        }
    }
}

// -*- coding: utf-8 -*-
// Version 3.0.0
using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace SpatialMorphology
{
    /// <summary>
    /// Counts how many of a voxel's six face-neighbours are filled.
    /// </summary>
    /// <remarks>
    /// RENAMED in 3.0.0 — was SA_Adjacency / SA_Adj / label "adjacency".
    /// The ComponentGuid is deliberately UNCHANGED so that definitions saved
    /// against the old name still resolve to this class instead of appearing
    /// as unrecognised objects.
    ///
    /// The default label moved from "adjacency" to "neighbor_count". That
    /// string is a channel key downstream: AnalysisStack matches channels by
    /// label, and ValueSet stores per-channel multipliers under it. Any saved
    /// ValueSet matrix holding weights for "adjacency" will not find them under
    /// the new key — re-open the ValueSet editor and confirm the column, or
    /// type "adjacency" into this component's label input to keep the old key.
    /// </remarks>
    public class NeighborCountComponent : SAComponentBase
    {
        // ── Constructor ───────────────────────────────────────────────────────
        public NeighborCountComponent()
            : base(
                "Neighbor Count",
                "NCount",
                "Counts how many of each voxel's six face-neighbours are filled.\n\n" +
                "  0 = no filled neighbours (isolated / exposed)\n" +
                "  6 = all six face-neighbours filled (fully interior)\n\n" +
                "invert = False (default): few neighbours = low value, many = high value\n" +
                "invert = True: few neighbours = high value, many = low value\n\n" +
                "Normalization is handled downstream by AnalysisStack.\n\n" +
                "Version 3.0.0",
                "Spatial Morphology",
                "2 | Analysis")
        { }

        // ── GUID — DO NOT CHANGE ──────────────────────────────────────────────
        // Kept from the SA_Adjacency era so old .gh files keep working.
        public override Guid ComponentGuid =>
            new Guid("E1F2A3B4-C5D6-7890-EFAB-012345678904");

        // ── Icon ──────────────────────────────────────────────────────────────
        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "SpatialMorphology.Resources.NeighborCount_24.png");
                return stream != null ? new Bitmap(stream) : null;
            }
        }
        public override GH_Exposure Exposure => GH_Exposure.secondary;
        // ── Parameters ────────────────────────────────────────────────────────
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("voxel_grid", "VG",
                "VoxelGrid object from the VoxelGrid component.",
                GH_ParamAccess.item);
            pManager.AddBooleanParameter("invert", "I",
                "If True, reverse the counts so few neighbours score high and\n" +
                "many neighbours score low.\n" +
                "Default: false.",
                GH_ParamAccess.item, false);
            pManager.AddTextParameter("label", "L",
                "Channel name used by AnalysisStack. Default: 'neighbor_count'.",
                GH_ParamAccess.item, "neighbor_count");
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("analysis", "A",
                "SpatialAnalysis object. Wire into AnalysisStack.",
                GH_ParamAccess.item);
            pManager.AddNumberParameter("values", "V",
                "Per-voxel neighbour counts, inverted if Invert is true.",
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
            bool invert = false;
            string label = "neighbor_count";

            if (!DA.GetData(0, ref voxelGridObj)) return;
            DA.GetData(1, ref invert);
            DA.GetData(2, ref label);

            var voxelGrid = UnwrapVoxelGrid(voxelGridObj);
            if (voxelGrid == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Could not read VoxelGrid object.");
                return;
            }

            string resolvedLabel = string.IsNullOrWhiteSpace(label)
                ? "neighbor_count" : label.Trim();

            // ── Count filled face-neighbours per voxel (0-6) ──────────────────
            var orderedKeys = voxelGrid.FilledKeys;
            var raw = new List<double>(orderedKeys.Count);
            foreach (var key in orderedKeys)
                raw.Add(voxelGrid.AdjacencyCount(key));

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

            string info = string.Format(
                "Neighbor Count | label='{0}' | voxels={1} | invert={2}\n" +
                "raw=[{3:F0} to {4:F0}] | output=[{5:F0} to {6:F0}] (counts, not normalized)",
                resolvedLabel, raw.Count, invert,
                rawMin, rawMax, outputMin, outputMax);

            DA.SetData(0, analysis);
            DA.SetDataList(1, outputValues);
            DA.SetDataList(2, centers);
            DA.SetDataList(3, gradient);
            DA.SetData(4, info);
        }
    }
}

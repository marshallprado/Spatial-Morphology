// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Drawing;
using Cumulus.Components.Evaluation;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace Cumulus
{
    /// <summary>
    /// Scores how closely each voxel's aggregate open-face normal aligns with
    /// a user-supplied world-space target vector.
    /// </summary>
    public sealed class SurfaceAlignmentComponent : SAComponentBase
    {
        public SurfaceAlignmentComponent()
            : base(
                "Surface Alignment",
                "SurfAlign",
                "Scores how closely each voxel faces a world-space target vector.\n\n" +
                "The score is the dot product between the aggregate normal of all " +
                "open voxel faces and the unitized target vector:\n" +
                "1 = faces target, 0 = perpendicular/neutral, -1 = faces away.\n\n" +
                "Unlike categorical Surface Direction, this continuous output is " +
                "safe to use with AnalysisStack.",
                "Cumulus",
                "2 | Analysis")
        {
        }
        public override GH_Exposure Exposure => GH_Exposure.secondary;
        public override Guid ComponentGuid =>
            new Guid("A7491DE8-E18B-48D4-AE04-2E95C4EB3F02");

        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream(
                    "Cumulus.Resources.SurfaceAlignment_24.png");
                return stream != null ? new Bitmap(stream) : null!;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "voxel_grid",
                "VG",
                "VoxelGrid object from the VoxelGrid component.",
                GH_ParamAccess.item);

            pManager.AddVectorParameter(
                "target",
                "T",
                "World-space direction against which open-face alignment is scored. " +
                "Magnitude is ignored. Default is world south: (0, -1, 0).",
                GH_ParamAccess.item,
                new Vector3d(0.0, -1.0, 0.0));

            pManager.AddBooleanParameter(
                "invert",
                "I",
                "If true, reverse the output range so facing away from the target " +
                "scores high.",
                GH_ParamAccess.item,
                false);

            pManager.AddTextParameter(
                "label",
                "L",
                "Analysis-channel label used by AnalysisStack.",
                GH_ParamAccess.item,
                "surface_alignment");
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "analysis",
                "A",
                "SpatialAnalysis object. Wire into AnalysisStack.",
                GH_ParamAccess.item);

            pManager.AddNumberParameter(
                "values",
                "V",
                "Alignment score per voxel, from -1 through +1 before inversion.",
                GH_ParamAccess.list);

            pManager.AddPointParameter(
                "centers",
                "C",
                "Voxel centres parallel to V.",
                GH_ParamAccess.list);

            pManager.AddColourParameter(
                "gradient",
                "G",
                "Per-voxel gradient color from low (red) to high (blue).",
                GH_ParamAccess.list);

            pManager.AddVectorParameter(
                "aggregate_normals",
                "D",
                "Unit aggregate open-face normal per voxel. It can be diagonal. " +
                "Interior or cancelling-normal voxels return zero.",
                GH_ParamAccess.list);

            pManager.AddIntegerParameter(
                "open_face_counts",
                "OF",
                "Number of open voxel faces, from 0 through 6.",
                GH_ParamAccess.list);

            pManager.AddTextParameter(
                "info",
                "I",
                "Alignment-analysis summary.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            object voxelGridObject = null;
            var target = new Vector3d(0.0, -1.0, 0.0);
            bool invert = false;
            string label = "surface_alignment";

            if (!DA.GetData(0, ref voxelGridObject))
                return;

            DA.GetData(1, ref target);
            DA.GetData(2, ref invert);
            DA.GetData(3, ref label);

            var voxelGrid = UnwrapVoxelGrid(voxelGridObject);
            if (voxelGrid == null)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Could not read VoxelGrid object.");
                return;
            }

            if (target.Length < 1e-12)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Target is a zero-length vector. Supply a valid direction.");
                return;
            }

            target.Unitize();

            string resolvedLabel = string.IsNullOrWhiteSpace(label)
                ? "surface_alignment"
                : label.Trim();

            var evaluation = VoxelOrientationEvaluator.Evaluate(voxelGrid);
            var rawValues = new List<double>(evaluation.AggregateNormals.Count);

            foreach (var normal in evaluation.AggregateNormals)
            {
                // Interior voxels have no open face and remain the least aligned
                // result, matching the legacy alignment behavior.
                if (normal.Length < 1e-12)
                {
                    rawValues.Add(-1.0);
                    continue;
                }

                rawValues.Add(normal * target);
            }

            var outputValues = invert ? InvertValues(rawValues) : rawValues;
            var analysis = new SpatialAnalysis(resolvedLabel, outputValues);

            var centers = new List<Point3d>(voxelGrid.FilledKeys.Count);
            foreach (var key in voxelGrid.FilledKeys)
                centers.Add(voxelGrid.KeyToCenter(key));

            var gradient = ComputeGradient(outputValues);
            BuildPreviewData(voxelGrid, outputValues);

            GetStatistics(outputValues, out double minimum, out double maximum, out double mean);

            string info = string.Format(
                "Surface Alignment | label='{0}' | invert={1}\n" +
                "target=({2:F3}, {3:F3}, {4:F3}) world\n" +
                "voxels={5} | interior={6} | aggregate_normal_cancels={7}\n" +
                "output=[{8:F4} to {9:F4}] | mean={10:F4} (cosine alignment)",
                resolvedLabel,
                invert,
                target.X,
                target.Y,
                target.Z,
                outputValues.Count,
                evaluation.InteriorCount,
                evaluation.CancellingNormalCount,
                minimum,
                maximum,
                mean);

            DA.SetData(0, analysis);
            DA.SetDataList(1, outputValues);
            DA.SetDataList(2, centers);
            DA.SetDataList(3, gradient);
            DA.SetDataList(4, evaluation.AggregateNormals);
            DA.SetDataList(5, evaluation.OpenFaceCounts);
            DA.SetData(6, info);
        }

        private static void GetStatistics(
            IReadOnlyList<double> values,
            out double minimum,
            out double maximum,
            out double mean)
        {
            if (values.Count == 0)
            {
                minimum = 0.0;
                maximum = 0.0;
                mean = 0.0;
                return;
            }

            minimum = values[0];
            maximum = values[0];
            double sum = 0.0;

            foreach (double value in values)
            {
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
                sum += value;
            }

            mean = sum / values.Count;
        }
    }
}

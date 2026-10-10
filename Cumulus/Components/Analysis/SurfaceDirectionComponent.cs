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
    /// Reports the dominant open face direction for each voxel. Direction
    /// categories are diagnostics and intentionally do not produce SpatialAnalysis.
    /// </summary>
    public sealed class SurfaceDirectionComponent : SAComponentBase
    {
        public SurfaceDirectionComponent()
            : base(
                "Surface Direction",
                "SurfDir",
                "Reports the dominant open face direction of each voxel.\n\n" +
                "This is categorical diagnostic data, not a scalar performance " +
                "analysis. It intentionally has no Analysis output, preventing " +
                "direction IDs from being mistakenly normalized by AnalysisStack.",
                "Cumulus",
                "2 | Analysis")
        {
        }
        public override GH_Exposure Exposure => GH_Exposure.secondary;
        public override Guid ComponentGuid =>
            new Guid("F084ABD8-2C5A-40BE-9DB3-5C97E398A878");

        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream(
                    "Cumulus.Resources.Orientation_24.png");
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
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddIntegerParameter(
                "direction_indices",
                "DI",
                "Dominant open direction per voxel:\n" +
                "0=+X East, 1=-X West, 2=+Y North, 3=-Y South, " +
                "4=+Z Up, 5=-Z Down, -1=interior.",
                GH_ParamAccess.list);

            pManager.AddVectorParameter(
                "direction_vectors",
                "D",
                "Dominant axis vector per voxel. Interior voxels return zero.",
                GH_ParamAccess.list);

            pManager.AddIntegerParameter(
                "open_face_counts",
                "OF",
                "Number of open voxel faces, from 0 through 6.",
                GH_ParamAccess.list);

            pManager.AddPointParameter(
                "centers",
                "C",
                "Voxel centres parallel to the other outputs.",
                GH_ParamAccess.list);

            pManager.AddColourParameter(
                "category_colors",
                "G",
                "Fixed diagnostic color for each dominant direction category.",
                GH_ParamAccess.list);

            pManager.AddTextParameter(
                "info",
                "I",
                "Direction-category summary.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            object voxelGridObject = null;
            if (!DA.GetData(0, ref voxelGridObject))
                return;

            var voxelGrid = UnwrapVoxelGrid(voxelGridObject);
            if (voxelGrid == null)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Could not read VoxelGrid object.");
                return;
            }

            var evaluation = VoxelOrientationEvaluator.Evaluate(voxelGrid);

            var dominantVectors = new List<Vector3d>(evaluation.DirectionIndices.Count);
            foreach (int directionIndex in evaluation.DirectionIndices)
            {
                dominantVectors.Add(
                    directionIndex < 0
                        ? Vector3d.Zero
                        : VoxelOrientationEvaluator.DirectionVectors[directionIndex]);
            }

            var centers = new List<Point3d>(voxelGrid.FilledKeys.Count);
            foreach (var key in voxelGrid.FilledKeys)
                centers.Add(voxelGrid.KeyToCenter(key));

            // Use the fixed categorical colors in the component preview.
            _previewPoints = centers;
            _previewColors = new List<Color>(evaluation.CategoryColors);

            DA.SetDataList(0, evaluation.DirectionIndices);
            DA.SetDataList(1, dominantVectors);
            DA.SetDataList(2, evaluation.OpenFaceCounts);
            DA.SetDataList(3, centers);
            DA.SetDataList(4, evaluation.CategoryColors);
            DA.SetData(5, BuildInfo(evaluation));
        }

        private static string BuildInfo(OrientationEvaluation evaluation)
        {
            var lines = new System.Text.StringBuilder();

            lines.AppendLine(
                "Surface Direction | categorical diagnostic output; " +
                "do not use as an AnalysisStack score.");
            lines.AppendLine(
                $"voxels={evaluation.DirectionIndices.Count} | " +
                $"interior={evaluation.InteriorCount} | " +
                $"aggregate_normal_cancels={evaluation.CancellingNormalCount}");
            lines.AppendLine();
            lines.AppendLine("Dominant direction counts:");

            for (int direction = 0; direction < 6; direction++)
            {
                double percentage = evaluation.DirectionIndices.Count == 0
                    ? 0.0
                    : evaluation.DirectionCounts[direction] * 100.0 /
                      evaluation.DirectionIndices.Count;

                lines.AppendLine(
                    $"  {VoxelOrientationEvaluator.DirectionNames[direction],-12} " +
                    $"{evaluation.DirectionCounts[direction],7} ({percentage,5:F1}%)");
            }

            return lines.ToString().TrimEnd();
        }
    }
}

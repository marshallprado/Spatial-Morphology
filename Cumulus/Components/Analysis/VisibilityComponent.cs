// -*- coding: utf-8 -*-
// Version 4.0.0
using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace Cumulus
{
    /// <summary>
    /// How many specific target points each voxel centre can see, tested
    /// against obstruction geometry.
    /// </summary>
    /// <remarks>
    /// RENAMED in 4.0.0 — was SA_ViewAnalysis / SA_View / label "view".
    /// The ComponentGuid is deliberately UNCHANGED so saved definitions still
    /// resolve to this class rather than appearing as unrecognised objects.
    ///
    /// The default label moved from "view" to "visibility". That string is a
    /// channel key downstream: AnalysisStack matches channels by label and
    /// ValueSet stores per-channel multipliers under it. Any saved ValueSet
    /// matrix holding weights for "view" will not find them under the new key
    /// — re-open the ValueSet editor and confirm the column, or type "view"
    /// into this component's label input to keep the old key.
    ///
    /// COLLISION CLEARED — "view" is now the default label of the View
    /// component (formerly SA_Isovist). Before this rename both components
    /// defaulted to "view" and AnalysisStack rejected them as duplicates when
    /// both were wired into the same stack. They measure different things:
    ///
    ///   View          how open is the space around me — ray sum, no targets.
    ///
    ///   Visibility    how many specific target points can I see — needs a
    ///   (this)        list of view_points.
    ///
    /// The two are complementary rather than redundant. A voxel deep in an
    /// atrium scores high on View and low on Visibility if the targets are
    /// all outside the building. Both can feed one stack.
    /// </remarks>
    public class VisibilityComponent : SAComponentBase
    {
        // -- Constructor -------------------------------------------------------
        public VisibilityComponent()
            : base(
                "Visibility",
                "Vis",
                "How many target points each voxel centre can see.\n\n" +
                "A ray is cast from the voxel centre toward every point in\n" +
                "view_points. The ray is blocked if it hits the obstruction\n" +
                "mesh before reaching the target. The output is the COUNT of\n" +
                "targets that remain visible.\n\n" +
                "  high value = sees many targets\n" +
                "  low value  = sees few or none\n\n" +
                "Use for view corridors, landmark sightlines, amenity\n" +
                "exposure, or any question of the form 'can I see that\n" +
                "from here'.\n\n" +
                "view_points are the things being LOOKED AT, not viewer\n" +
                "positions — every filled voxel is a viewer.\n\n" +
                "invert = False (default): sees few = low value, sees many = high\n" +
                "invert = True: sees few = high value, sees many = low value\n" +
                "  Use when being seen is undesirable — screening a service\n" +
                "  yard, siting a private terrace out of sight.\n\n" +
                "This component needs TARGET POINTS. To measure the general\n" +
                "openness of space with no targets, use View.\n\n" +
                "Normalization is handled downstream by AnalysisStack.\n\n" +
                "Version 4.0.0",
                "Cumulus",
                "2 | Analysis")
        { }

        // -- Ribbon placement --------------------------------------------------
        // Contextual analysis — takes external target points and obstructions.
        public override GH_Exposure Exposure => GH_Exposure.primary;

        // -- GUID — DO NOT CHANGE ----------------------------------------------
        // Kept from the SA_ViewAnalysis era so old .gh files keep working.
        public override Guid ComponentGuid =>
            new Guid("E5F6A7B8-C9D0-1234-EFAB-012345678912");

        // -- Icon --------------------------------------------------------------
        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "Cumulus.Resources.Visibility_24.png");
                return stream != null ? new Bitmap(stream) : null;
            }
        }

        // -- Parameters --------------------------------------------------------
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("voxel_grid", "VG",
                "VoxelGrid object from the VoxelGrid component.",
                GH_ParamAccess.item);
            pManager.AddPointParameter("view_points", "VP",
                "Target points to measure visibility TOWARD.\n" +
                "Each voxel scores the number of these it can see.\n" +
                "These are the things being looked at — every filled voxel\n" +
                "is already a viewer position.",
                GH_ParamAccess.list);
            pManager.AddMeshParameter("obstruction_mesh", "O",
                "Meshes that block sightlines — building envelope or urban\n" +
                "context.\n" +
                "Optional — with none connected every target is visible from\n" +
                "every voxel and all voxels score the same.",
                GH_ParamAccess.list);
            pManager.AddBooleanParameter("invert", "I",
                "If True, reverse the values so voxels that see fewer targets\n" +
                "score high and voxels that see more score low.\n" +
                "Default: false.",
                GH_ParamAccess.item, false);
            pManager.AddTextParameter("label", "L",
                "Channel name used by AnalysisStack. Default: 'visibility'.",
                GH_ParamAccess.item, "visibility");

            pManager[2].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("analysis", "A",
                "SpatialAnalysis object. Wire into AnalysisStack.",
                GH_ParamAccess.item);
            pManager.AddNumberParameter("values", "V",
                "Per-voxel count of visible target points, inverted if Invert\n" +
                "is true.",
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

        // -- Solve -------------------------------------------------------------
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            object voxelGridObj = null;
            var viewPoints = new List<Point3d>();
            var obstructionMeshes = new List<Mesh>();
            bool invert = false;
            string label = "visibility";

            if (!DA.GetData(0, ref voxelGridObj)) return;
            if (!DA.GetDataList(1, viewPoints)) return;
            DA.GetDataList(2, obstructionMeshes);
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
                ? "visibility" : label.Trim();

            if (viewPoints.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Connect at least one view point.\n" +
                    "To measure openness with no targets, use View instead.");
                return;
            }

            // -- Merge obstruction meshes --------------------------------------
            Mesh obstructionMesh = null;
            if (obstructionMeshes.Count > 0)
            {
                obstructionMesh = new Mesh();
                foreach (var m in obstructionMeshes)
                    if (m != null) obstructionMesh.Append(m);
                obstructionMesh.Compact();
            }

            // With nothing to block a sightline every voxel sees every target,
            // which produces a flat channel that looks valid but carries no
            // information. Say so rather than letting it pass silently.
            if (obstructionMesh == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "No obstruction mesh connected — every target is visible " +
                    "from every voxel, so all voxels score the same.");
            }

            // -- Count visible targets per voxel -------------------------------
            var orderedKeys = voxelGrid.FilledKeys;
            var raw = new List<double>(orderedKeys.Count);
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

                    // Target sits inside this voxel — trivially visible.
                    if (length < nudge)
                    {
                        visible++;
                        continue;
                    }

                    dir.Unitize();
                    bool blocked = false;

                    if (obstructionMesh != null)
                    {
                        double hit = Intersection.MeshRay(
                            obstructionMesh, new Ray3d(origin, dir));

                        // Only a hit BETWEEN origin and target blocks the view.
                        // A hit beyond the target is irrelevant, and the nudge
                        // keeps a voxel sitting on the mesh from blocking
                        // itself.
                        if (hit >= nudge && hit < length - nudge)
                            blocked = true;
                    }

                    if (!blocked) visible++;
                }

                raw.Add(visible);
            }

            // -- Invert if requested, then keep values/colors/preview in sync --
            var outputValues = invert ? InvertValues(raw) : raw;
            var analysis = new SpatialAnalysis(resolvedLabel, outputValues);

            var centers = new List<Point3d>(orderedKeys.Count);
            foreach (var key in orderedKeys)
                centers.Add(voxelGrid.KeyToCenter(key));

            var gradient = ComputeGradient(outputValues);
            BuildPreviewData(voxelGrid, outputValues);

            // -- Stats ---------------------------------------------------------
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
            double pctVisible = nTargets > 0 ? rawMean / nTargets * 100.0 : 0.0;

            string info = string.Format(
                "Visibility | label='{0}' | voxels={1} | invert={2}\n" +
                "view_points={3} | obstructions={4}\n" +
                "raw=[{5:F0} to {6:F0}] | output=[{7:F0} to {8:F0}]\n" +
                "avg_visible={9:F1} of {3} targets ({10:F1}%)\n" +
                "(visible point count, not normalized)",
                resolvedLabel, raw.Count, invert,
                nTargets,
                obstructionMesh != null
                    ? obstructionMeshes.Count + " mesh(es)"
                    : "none",
                rawMin, rawMax, outputMin, outputMax,
                rawMean, pctVisible);

            DA.SetData(0, analysis);
            DA.SetDataList(1, outputValues);
            DA.SetDataList(2, centers);
            DA.SetDataList(3, gradient);
            DA.SetData(4, info);
        }
    }
}

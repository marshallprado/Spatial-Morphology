// -*- coding: utf-8 -*-
// Version 3.0.0
using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace Cumulus.Components.Legacy
{
    /// <summary>
    /// Openness of the space around each voxel centre, measured by casting
    /// rays until they hit context geometry.
    /// </summary>
    /// <remarks>
    /// RENAMED in 3.0.0 — was SA_Isovist / SA_Iso / label "isovist".
    /// The ComponentGuid is deliberately UNCHANGED so saved definitions still
    /// resolve to this class rather than appearing as unrecognised objects.
    ///
    /// The default label moved from "isovist" to "view". That string is a
    /// channel key downstream: AnalysisStack matches channels by label and
    /// ValueSet stores per-channel multipliers under it. Any saved ValueSet
    /// matrix holding weights for "isovist" will not find them under the new
    /// key — re-open the ValueSet editor and confirm the column, or type
    /// "isovist" into this component's label input to keep the old key.
    ///
    /// NAME COLLISION — the label "view" was previously the default for
    /// SA_ViewAnalysis, which is being renamed to Visibility with the label
    /// "visibility". Until that rename lands, both components default to
    /// "view" and AnalysisStack will reject them if BOTH are wired into the
    /// same stack. They are different measurements:
    ///
    ///   View (this)   how open is the space around me — ray sum, no targets.
    ///
    ///   Visibility    how many specific target points can I see — needs a
    ///                 list of view_points.
    /// </remarks>
    public class ViewComponent : SAComponentBase
    {
        // -- Constructor -------------------------------------------------------
        public ViewComponent()
            : base(
                "View",
                "View",
                "Openness of the space around each voxel centre.\n\n" +
                "Rays are cast outward from the voxel centre. Each ray travels\n" +
                "until it hits an obstacle or reaches the radius limit. The\n" +
                "output is the SUM of all ray distances.\n\n" +
                "  high value = open space  (rays travel far)\n" +
                "  low value  = enclosed    (rays blocked early)\n\n" +
                "Mode 0 — Spherical (3D):\n" +
                "  Rays spread uniformly over the full sphere, Fibonacci\n" +
                "  distribution. Use for atria, vertical connection, sky\n" +
                "  exposure. Recommend 100-500 rays.\n\n" +
                "Mode 1 — Planar (2D):\n" +
                "  Rays spread uniformly in the horizontal plane only, aligned\n" +
                "  to the VoxelGrid construction plane. Use for room\n" +
                "  connectivity, street visibility, urban space quality.\n" +
                "  Recommend 36-72 rays — roughly 16x fewer than spherical for\n" +
                "  the same angular resolution.\n\n" +
                "Voxels below the construction plane are set to 0 without\n" +
                "casting any rays, which speeds up models with below-grade\n" +
                "levels.\n\n" +
                "invert = False (default): enclosed = low value, open = high\n" +
                "invert = True: enclosed = high value, open = low value\n" +
                "  Use when enclosure is desirable — a screening room, a\n" +
                "  server core, a quiet cell.\n\n" +
                "This component needs NO target points. To measure visibility\n" +
                "toward specific points, use Visibility.\n\n" +
                "Normalization is handled downstream by AnalysisStack.\n\n" +
                "Version 3.0.0",
                "Cumulus",
                "2 | Analysis")
        { }

        // -- Ribbon placement --------------------------------------------------
        // Contextual analysis — takes external obstacle geometry as input.
        public override GH_Exposure Exposure => GH_Exposure.hidden;

        // -- GUID — DO NOT CHANGE ----------------------------------------------
        // Kept from the SA_Isovist era so old .gh files keep working.
        public override Guid ComponentGuid =>
            new Guid("F6A7B8C9-D0E1-2345-FABC-012345678913");

        // -- Icon --------------------------------------------------------------
        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "Cumulus.Resources.View_24.png");
                return stream != null ? new Bitmap(stream) : null;
            }
        }

        // -- Parameters --------------------------------------------------------
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("voxel_grid", "VG",
                "VoxelGrid object from the VoxelGrid component.",
                GH_ParamAccess.item);
            pManager.AddMeshParameter("obstacles", "O",
                "Obstacle meshes that block the rays.\n" +
                "Typically the building envelope or urban context.\n" +
                "Optional — with none connected every ray runs the full\n" +
                "radius and all voxels score the same.",
                GH_ParamAccess.list);
            pManager.AddIntegerParameter("sample_count", "S",
                "Number of rays per voxel.\n" +
                "  Mode 0 (Spherical): recommend 100-500\n" +
                "  Mode 1 (Planar):    recommend 36-72\n" +
                "Default: 100.",
                GH_ParamAccess.item, 100);
            pManager.AddNumberParameter("radius", "R",
                "Maximum ray distance in model units.\n" +
                "Rays that hit nothing are clamped to this value.\n" +
                "Default: 1000.",
                GH_ParamAccess.item, 1000.0);
            pManager.AddIntegerParameter("mode", "M",
                "Ray distribution:\n" +
                "  0 = Spherical (3D) — full sphere, Fibonacci distribution\n" +
                "  1 = Planar (2D)    — horizontal plane, uniform spacing\n" +
                "Default: 1.",
                GH_ParamAccess.item, 1);
            pManager.AddBooleanParameter("invert", "I",
                "If True, reverse the values so enclosed space scores high and\n" +
                "open space scores low.\n" +
                "Default: false.",
                GH_ParamAccess.item, false);
            pManager.AddTextParameter("label", "L",
                "Channel name used by AnalysisStack. Default: 'view'.",
                GH_ParamAccess.item, "view");

            pManager[1].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("analysis", "A",
                "SpatialAnalysis object. Wire into AnalysisStack.",
                GH_ParamAccess.item);
            pManager.AddNumberParameter("values", "V",
                "Per-voxel sum of ray distances, inverted if Invert is true.\n" +
                "0 = below construction plane, or fully enclosed.",
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
            var obstacleMeshes = new List<Mesh>();
            int sampleCount = 100;
            double radius = 1000.0;
            int mode = 1;
            bool invert = false;
            string label = "view";

            if (!DA.GetData(0, ref voxelGridObj)) return;
            DA.GetDataList(1, obstacleMeshes);
            DA.GetData(2, ref sampleCount);
            DA.GetData(3, ref radius);
            DA.GetData(4, ref mode);
            DA.GetData(5, ref invert);
            DA.GetData(6, ref label);

            var voxelGrid = UnwrapVoxelGrid(voxelGridObj);
            if (voxelGrid == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Could not read VoxelGrid object.");
                return;
            }

            string resolvedLabel = string.IsNullOrWhiteSpace(label)
                ? "view" : label.Trim();

            sampleCount = Math.Max(4, sampleCount);
            radius = Math.Max(1.0, radius);
            mode = Math.Max(0, Math.Min(1, mode));

            // -- Merge obstacles into one mesh for faster ray queries ----------
            Mesh obstacleMesh = null;
            if (obstacleMeshes.Count > 0)
            {
                obstacleMesh = new Mesh();
                foreach (var mesh in obstacleMeshes)
                    if (mesh != null) obstacleMesh.Append(mesh);
                obstacleMesh.Compact();
            }

            if (obstacleMesh == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "No obstacles connected — every ray runs the full radius, " +
                    "so all above-plane voxels score identically.");
            }

            // -- Construction plane Z axis, needed for planar mode -------------
            var planeZAxis = new Vector3d(
                voxelGrid.PlaneToWorld.M02,
                voxelGrid.PlaneToWorld.M12,
                voxelGrid.PlaneToWorld.M22);
            planeZAxis.Unitize();

            List<Vector3d> directions = mode == 0
                ? FibonacciSphere(sampleCount)
                : PlanarCircle(sampleCount, planeZAxis);

            // -- Cast rays per voxel -------------------------------------------
            var orderedKeys = voxelGrid.FilledKeys;
            var raw = new List<double>(orderedKeys.Count);
            int nBelowPlane = 0;
            int nAbovePlane = 0;

            foreach (var key in orderedKeys)
            {
                // Below-grade voxels short-circuit to 0 — no rays cast.
                if (voxelGrid.IsBelowGrade(key))
                {
                    raw.Add(0.0);
                    nBelowPlane++;
                    continue;
                }

                nAbovePlane++;
                Point3d origin = voxelGrid.KeyToCenter(key);
                double sum = 0.0;

                foreach (var direction in directions)
                {
                    double distance = radius;

                    if (obstacleMesh != null)
                    {
                        double hit = Intersection.MeshRay(
                            obstacleMesh, new Ray3d(origin, direction));
                        if (hit >= 0) distance = Math.Min(hit, radius);
                    }

                    sum += distance;
                }

                raw.Add(sum);
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
            double maxPossible = radius * sampleCount;

            string[] modeNames = { "Spherical (3D)", "Planar (2D)" };
            string info = string.Format(
                "View | label='{0}' | mode={1} ({2}) | invert={3}\n" +
                "voxels={4} | rays={5} | radius={6:F1}\n" +
                "above_plane={7} | below_plane={8} (value=0, rays skipped)\n" +
                "max_possible={9:F1} | raw=[{10:F1} to {11:F1}] | raw_mean={12:F1}\n" +
                "output=[{13:F1} to {14:F1}] (sum of ray distances, not normalized)\n" +
                "obstacles={15}",
                resolvedLabel, mode, modeNames[mode], invert,
                raw.Count, sampleCount, radius,
                nAbovePlane, nBelowPlane,
                maxPossible, rawMin, rawMax, rawMean,
                outputMin, outputMax,
                obstacleMesh != null
                    ? obstacleMeshes.Count + " mesh(es)"
                    : "none");

            DA.SetData(0, analysis);
            DA.SetDataList(1, outputValues);
            DA.SetDataList(2, centers);
            DA.SetDataList(3, gradient);
            DA.SetData(4, info);
        }

        // -- Mode 0 — Fibonacci sphere, uniform 3D distribution ----------------
        private List<Vector3d> FibonacciSphere(int count)
        {
            var directions = new List<Vector3d>(count);
            double phi = Math.PI * (3.0 - Math.Sqrt(5.0));

            for (int i = 0; i < count; i++)
            {
                double y = 1.0 - (i / (double)(count - 1)) * 2.0;
                double rxy = Math.Sqrt(Math.Max(0.0, 1.0 - y * y));
                double theta = phi * i;

                var direction = new Vector3d(
                    Math.Cos(theta) * rxy,
                    y,
                    Math.Sin(theta) * rxy);
                direction.Unitize();
                directions.Add(direction);
            }

            return directions;
        }

        // -- Mode 1 — Planar circle, uniform 2D distribution -------------------
        private List<Vector3d> PlanarCircle(int count, Vector3d zAxis)
        {
            var directions = new List<Vector3d>(count);

            // Build an in-plane basis. The fallback handles a plane whose Z
            // already runs parallel to world Z, where the first cross product
            // degenerates to zero length.
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

                var direction = new Vector3d(
                    cosA * xAxis.X + sinA * yAxis.X,
                    cosA * xAxis.Y + sinA * yAxis.Y,
                    cosA * xAxis.Z + sinA * yAxis.Z);
                direction.Unitize();
                directions.Add(direction);
            }

            return directions;
        }
    }
}

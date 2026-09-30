// -*- coding: utf-8 -*-
// Version 4.0.0
using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace SpatialMorphology
{
    /// <summary>
    /// Distance from each voxel centre to the nearest object in a list of
    /// context geometry.
    /// </summary>
    /// <remarks>
    /// RENAMED in 4.0.0 — was SA_GeometryDistance / SA_GeoDist / label
    /// "geo_dist". The ComponentGuid is deliberately UNCHANGED so saved
    /// definitions still resolve to this class rather than appearing as
    /// unrecognised objects.
    ///
    /// The name "Proximity" was previously used by SA_Proximity, which measured
    /// distance to the voxel SHELL. That component was merged into Surface
    /// Distance (mode 1, now called "Metric"), which freed this name. The two
    /// are not interchangeable:
    ///
    ///   Surface Distance   distance to the voxel shell — no geometry input,
    ///                      answers "how deep inside the mass am I".
    ///
    ///   Proximity (this)   distance to arbitrary context geometry — answers
    ///                      "how far am I from that park / core / facade".
    ///
    /// The default label moved from "geo_dist" to "proximity". That string is a
    /// channel key downstream: AnalysisStack matches channels by label and
    /// ValueSet stores per-channel multipliers under it. Any saved ValueSet
    /// matrix holding weights for "geo_dist" will not find them under the new
    /// key — re-open the ValueSet editor and confirm the column, or type
    /// "geo_dist" into this component's label input to keep the old key.
    /// </remarks>
    public class ProximityComponent : SAComponentBase
    {
        // ── Constructor ───────────────────────────────────────────────────────
        public ProximityComponent()
            : base(
                "Proximity",
                "Prox",
                "Distance from each voxel centre to the closest object in a\n" +
                "geometry list, in model units.\n\n" +
                "  0.0 = voxel centre sits exactly on the geometry\n" +
                "  N   = N model units from the nearest object\n\n" +
                "invert = False (default):\n" +
                "  close = low value, far = high value\n\n" +
                "invert = True:\n" +
                "  close = high value, far = low value\n" +
                "  Use when nearness is desirable — proximity to a park,\n" +
                "  an entrance, a view corridor, a circulation core.\n\n" +
                "Supported types: Point3d, Curve, Surface, Brep, Mesh,\n" +
                "Extrusion, and their Grasshopper wrappers.\n\n" +
                "This component measures distance to CONTEXT GEOMETRY you\n" +
                "supply. For distance to the voxel shell itself, use Surface\n" +
                "Distance.\n\n" +
                "Normalization is handled downstream by AnalysisStack.\n\n" +
                "Version 4.0.0",
                "Spatial Morphology",
                "2 | Analysis")
        { }

        // ── Ribbon placement ──────────────────────────────────────────────────
        // Contextual analysis — takes external geometry as input.
        public override GH_Exposure Exposure => GH_Exposure.primary;

        // ── GUID — DO NOT CHANGE ──────────────────────────────────────────────
        // Kept from the SA_GeometryDistance era so old .gh files keep working.
        public override Guid ComponentGuid =>
            new Guid("B2C3D4E5-F6A7-8901-BCDE-012345678907");

        // ── Icon ──────────────────────────────────────────────────────────────
        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "SpatialMorphology.Resources.Proximity_24.png");
                return stream != null ? new Bitmap(stream) : null;
            }
        }

        // ── Parameters ────────────────────────────────────────────────────────
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("voxel_grid", "VG",
                "VoxelGrid object from the VoxelGrid component.",
                GH_ParamAccess.item);
            pManager.AddGenericParameter("geometries", "G",
                "One or more Rhino geometry objects to measure distance to.\n" +
                "Supported: Point3d, Curve, Surface, Brep, Mesh, Extrusion.",
                GH_ParamAccess.list);
            pManager.AddBooleanParameter("invert", "I",
                "If True, reverse the values so close = high and far = low.\n" +
                "Default: false.",
                GH_ParamAccess.item, false);
            pManager.AddTextParameter("label", "L",
                "Channel name used by AnalysisStack. Default: 'proximity'.",
                GH_ParamAccess.item, "proximity");
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("analysis", "A",
                "SpatialAnalysis object. Wire into AnalysisStack.",
                GH_ParamAccess.item);
            pManager.AddNumberParameter("values", "V",
                "Per-voxel distance in model units, inverted if Invert is true.",
                GH_ParamAccess.list);
            pManager.AddPointParameter("centers", "C",
                "Voxel centres for preview.",
                GH_ParamAccess.list);
            pManager.AddColourParameter("gradient", "G",
                "Per-voxel gradient color from low (red) to high (blue),\n" +
                "based on the output values.",
                GH_ParamAccess.list);
            pManager.AddNumberParameter("raw_distances", "R",
                "Unmodified world-unit distances. Never inverted.",
                GH_ParamAccess.list);
            pManager.AddTextParameter("info", "I",
                "Summary.",
                GH_ParamAccess.item);
        }

        // ── Solve ─────────────────────────────────────────────────────────────
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            object voxelGridObj = null;
            var geometryObjects = new List<object>();
            bool invert = false;
            string label = "proximity";

            if (!DA.GetData(0, ref voxelGridObj)) return;
            if (!DA.GetDataList(1, geometryObjects)) return;
            DA.GetData(2, ref invert);
            DA.GetData(3, ref label);

            var voxelGrid = UnwrapVoxelGrid(voxelGridObj);
            if (voxelGrid == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Could not read VoxelGrid object.");
                return;
            }

            if (geometryObjects.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Connect at least one geometry object to 'geometries'.");
                return;
            }

            string resolvedLabel = string.IsNullOrWhiteSpace(label)
                ? "proximity" : label.Trim();

            // ── Closest distance per voxel ────────────────────────────────────
            var orderedKeys = voxelGrid.FilledKeys;
            var rawDistances = new List<double>(orderedKeys.Count);

            foreach (var key in orderedKeys)
            {
                var pt = voxelGrid.KeyToCenter(key);
                double minDist = double.MaxValue;

                foreach (var obj in geometryObjects)
                {
                    double d = ClosestDistanceToObject(pt, obj);
                    if (d < minDist) minDist = d;
                }

                rawDistances.Add(minDist == double.MaxValue
                    ? double.PositiveInfinity
                    : minDist);
            }

            // ── Validate ──────────────────────────────────────────────────────
            bool hasFinite = false;
            double rawMin = double.MaxValue;
            double rawMax = double.MinValue;
            int nInfinite = 0;

            foreach (var value in rawDistances)
            {
                if (double.IsInfinity(value) || double.IsNaN(value))
                {
                    nInfinite++;
                    continue;
                }
                hasFinite = true;
                if (value < rawMin) rawMin = value;
                if (value > rawMax) rawMax = value;
            }

            if (!hasFinite)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "All distance queries returned infinity — check that the\n" +
                    "connected objects are supported geometry types.");
                return;
            }

            // ── Invert if requested, then keep values/colors/preview in sync ──
            // InvertValues preserves the range and maps non-finite entries to 0.
            var outputValues = invert
                ? InvertValues(rawDistances)
                : SanitizeNonFinite(rawDistances);

            var analysis = new SpatialAnalysis(resolvedLabel, outputValues);

            var centers = new List<Point3d>(orderedKeys.Count);
            foreach (var key in orderedKeys)
                centers.Add(voxelGrid.KeyToCenter(key));

            var gradient = ComputeGradient(outputValues);
            BuildPreviewData(voxelGrid, outputValues);

            // ── Geometry type summary ─────────────────────────────────────────
            var typeCounts = new Dictionary<string, int>();
            foreach (var obj in geometryObjects)
            {
                object inner = obj is Grasshopper.Kernel.Types.GH_ObjectWrapper w
                    ? w.Value : obj;
                string typeName = inner?.GetType().Name ?? "unknown";
                if (!typeCounts.ContainsKey(typeName)) typeCounts[typeName] = 0;
                typeCounts[typeName]++;
            }

            var typeParts = new List<string>();
            foreach (var kvp in typeCounts)
                typeParts.Add(string.Format("{0}x{1}", kvp.Value, kvp.Key));
            string typeSummary = string.Join(", ", typeParts);

            // ── Stats ─────────────────────────────────────────────────────────
            double outputMin = outputValues.Count > 0 ? outputValues[0] : 0.0;
            double outputMax = outputMin;
            foreach (var value in outputValues)
            {
                if (value < outputMin) outputMin = value;
                if (value > outputMax) outputMax = value;
            }

            string info = string.Format(
                "Proximity | label='{0}' | voxels={1} | invert={2}\n" +
                "geometries={3} [{4}]\n" +
                "raw=[{5:F4} to {6:F4}] | output=[{7:F4} to {8:F4}] " +
                "(model units, not normalized){9}",
                resolvedLabel, rawDistances.Count, invert,
                geometryObjects.Count, typeSummary,
                rawMin, rawMax, outputMin, outputMax,
                nInfinite > 0
                    ? string.Format(
                        "\nWARNING: {0} voxels returned infinite distance — " +
                        "unsupported geometry type?", nInfinite)
                    : "");

            DA.SetData(0, analysis);
            DA.SetDataList(1, outputValues);
            DA.SetDataList(2, centers);
            DA.SetDataList(3, gradient);
            DA.SetDataList(4, rawDistances);
            DA.SetData(5, info);
        }

        // ── Replace infinity / NaN with 0.0, leave everything else alone ──────
        private static List<double> SanitizeNonFinite(IList<double> values)
        {
            var clean = new List<double>(values.Count);
            foreach (var value in values)
            {
                clean.Add(double.IsInfinity(value) || double.IsNaN(value)
                    ? 0.0 : value);
            }
            return clean;
        }

        // ── Distance dispatcher ───────────────────────────────────────────────
        private double ClosestDistanceToObject(Point3d pt, object obj)
        {
            object inner = obj is Grasshopper.Kernel.Types.GH_ObjectWrapper wrapper
                ? wrapper.Value : obj;

            if (inner == null) return double.PositiveInfinity;

            try
            {
                // ── Point types ───────────────────────────────────────────────
                if (inner is Point3d p3d)
                    return pt.DistanceTo(p3d);

                if (inner is Grasshopper.Kernel.Types.GH_Point ghPt)
                    return pt.DistanceTo(ghPt.Value);

                if (inner is Rhino.Geometry.Point rhinoPt)
                    return pt.DistanceTo(rhinoPt.Location);

                // ── Curve types ───────────────────────────────────────────────
                Curve resolvedCurve = null;
                if (inner is Grasshopper.Kernel.Types.GH_Curve ghCurve)
                    resolvedCurve = ghCurve.Value;
                else if (inner is Curve nativeCurve)
                    resolvedCurve = nativeCurve;

                if (resolvedCurve != null)
                {
                    double t;
                    if (resolvedCurve.ClosestPoint(pt, out t))
                        return pt.DistanceTo(resolvedCurve.PointAt(t));
                    return double.PositiveInfinity;
                }

                // ── Mesh types ────────────────────────────────────────────────
                Mesh resolvedMesh = null;
                if (inner is Grasshopper.Kernel.Types.GH_Mesh ghMesh)
                    resolvedMesh = ghMesh.Value;
                else if (inner is Mesh nativeMesh)
                    resolvedMesh = nativeMesh;

                if (resolvedMesh != null)
                {
                    var mp = resolvedMesh.ClosestMeshPoint(pt, 0.0);
                    return mp != null
                        ? pt.DistanceTo(mp.Point)
                        : double.PositiveInfinity;
                }

                // ── Extrusion ─────────────────────────────────────────────────
                if (inner is Grasshopper.Kernel.Types.GH_Extrusion ghExt)
                {
                    Extrusion extVal = ghExt.Value;
                    if (extVal != null)
                    {
                        Brep extBrep = extVal.ToBrep(true);
                        if (extBrep != null)
                            return pt.DistanceTo(extBrep.ClosestPoint(pt));
                    }
                    return double.PositiveInfinity;
                }

                if (inner is Extrusion nativeExtrusion)
                {
                    Brep extBrep = nativeExtrusion.ToBrep(true);
                    if (extBrep != null)
                        return pt.DistanceTo(extBrep.ClosestPoint(pt));
                    return double.PositiveInfinity;
                }

                // ── Brep types ────────────────────────────────────────────────
                if (inner is Grasshopper.Kernel.Types.GH_Brep ghBrep)
                {
                    Brep brepVal = ghBrep.Value;
                    if (brepVal != null)
                        return pt.DistanceTo(brepVal.ClosestPoint(pt));
                    return double.PositiveInfinity;
                }

                if (inner is Brep nativeBrep)
                    return pt.DistanceTo(nativeBrep.ClosestPoint(pt));

                // ── Surface types ─────────────────────────────────────────────
                if (inner is Grasshopper.Kernel.Types.GH_Surface ghSurf)
                {
                    Brep surfBrep = ghSurf.Value as Brep;
                    if (surfBrep != null)
                        return pt.DistanceTo(surfBrep.ClosestPoint(pt));
                    return double.PositiveInfinity;
                }

                if (inner is Surface nativeSurface)
                {
                    Mesh surfMesh = Mesh.CreateFromSurface(
                        nativeSurface, MeshingParameters.Default);
                    if (surfMesh != null)
                    {
                        var mp = surfMesh.ClosestMeshPoint(pt, 0.0);
                        if (mp != null)
                            return pt.DistanceTo(mp.Point);
                    }
                    return double.PositiveInfinity;
                }
            }
            catch { }

            return double.PositiveInfinity;
        }
    }
}


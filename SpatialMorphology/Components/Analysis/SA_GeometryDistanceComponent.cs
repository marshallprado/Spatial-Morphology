// -*- coding: utf-8 -*-
// Version 3.0.0
using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace SpatialMorphology
{
    public class SA_GeometryDistanceComponent : SAComponentBase
    {
        // ── Constructor ───────────────────────────────────────────────────────
        public SA_GeometryDistanceComponent()
            : base(
                "SA_GeometryDistance",
                "SA_GeoDist",
                "Distance from each voxel centre to the closest object in a geometry list,\n" +
                "output as raw world-unit distance.\n\n" +
                "  0.0 = voxel centre sits exactly on the geometry\n" +
                "  N   = N model units from the nearest geometry object\n\n" +
                "invert = False (default):\n" +
                "  Low distance  = low performance value\n" +
                "  High distance = high performance value\n\n" +
                "invert = True:\n" +
                "  Low distance  = high performance value (closer = better)\n" +
                "  High distance = low performance value (further = worse)\n\n" +
                "Supported types: Point3d, Curve, Surface, Brep, Mesh\n\n" +
                "Normalization is handled downstream by AnalysisStack.\n\n" +
                "Version 3.0.0",
                "Spatial Morphology",
                "Analysis")
        { }

        // ── GUID ──────────────────────────────────────────────────────────────
        public override Guid ComponentGuid =>
            new Guid("B2C3D4E5-F6A7-8901-BCDE-012345678907");

        // ── Icon ──────────────────────────────────────────────────────────────
        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "SpatialMorphology.Resources.SA_GeoDist_24.png");
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
                "Supported: Point3d, Curve, Surface, Brep, Mesh.",
                GH_ParamAccess.list);
            pManager.AddBooleanParameter("invert", "I",
                "If True, invert the values so closer = higher performance.\n" +
                "  False (default) = low distance → low value, high distance → high value\n" +
                "  True            = low distance → high value, high distance → low value\n" +
                "Useful when proximity to geometry is desirable\n" +
                "(e.g. distance to a park, view, or amenity).",
                GH_ParamAccess.item, false);
            pManager.AddTextParameter("label", "L",
                "Channel name used by AnalysisStack. Default: 'geo_dist'.",
                GH_ParamAccess.item, "geo_dist");
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("analysis", "A",
                "SpatialAnalysis object. Wire into AnalysisStack.",
                GH_ParamAccess.item);
            pManager.AddNumberParameter("values", "V",
                "Per-voxel raw world-unit distances.\n" +
                "Inverted if invert=True.",
                GH_ParamAccess.list);
            pManager.AddPointParameter("centers", "C",
                "Voxel centres for preview.",
                GH_ParamAccess.list);
            pManager.AddColourParameter("gradient", "G",
                "Per-voxel gradient color.\n" +
                "invert=False: low distance = red, high distance = blue.\n" +
                "invert=True:  low distance = blue, high distance = red.",
                GH_ParamAccess.list);
            pManager.AddNumberParameter("raw_distances", "R",
                "Unremapped world-unit distances (never inverted).",
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
            var geometryObjects = new List<object>();
            bool invert = false;
            string label = "geo_dist";

            if (!DA.GetData(0, ref voxelGridObj)) return;
            if (!DA.GetDataList(1, geometryObjects)) return;
            DA.GetData(2, ref invert);
            DA.GetData(3, ref label);

            // ── Unwrap VoxelGrid ──────────────────────────────────────────────
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
                ? "geo_dist" : label.Trim();

            // ── Compute raw world-unit distances ──────────────────────────────
            var orderedKeys = voxelGrid.FilledKeys;
            var rawDistances = new List<double>();

            foreach (var key in orderedKeys)
            {
                var pt = voxelGrid.KeyToCenter(key);
                double minDist = double.MaxValue;

                foreach (var obj in geometryObjects)
                {
                    double d = ClosestDistanceToObject(pt, obj);
                    if (d < minDist) minDist = d;
                }

                rawDistances.Add(
                    minDist == double.MaxValue
                        ? double.PositiveInfinity
                        : minDist);
            }

            // ── Validate ──────────────────────────────────────────────────────
            bool hasFinite = false;
            double rawMin = double.MaxValue;
            double rawMax = double.MinValue;
            int nInf = 0;

            foreach (var v in rawDistances)
            {
                if (double.IsInfinity(v) || double.IsNaN(v))
                {
                    nInf++;
                    continue;
                }
                hasFinite = true;
                if (v < rawMin) rawMin = v;
                if (v > rawMax) rawMax = v;
            }

            if (!hasFinite)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "All distance queries returned infinity — check geometry types.");
                return;
            }

            // ── Build output values ───────────────────────────────────────────
            // If invert=True, flip the distances so closer = higher value
            // We invert by computing: invertedDist = rawMax - dist + rawMin
            // This preserves the relative spread but flips high/low
            var outputValues = new List<double>(rawDistances.Count);

            if (invert)
            {
                foreach (var v in rawDistances)
                {
                    if (double.IsInfinity(v) || double.IsNaN(v))
                        outputValues.Add(0.0);
                    else
                        outputValues.Add(rawMax - v + rawMin);
                }
            }
            else
            {
                foreach (var v in rawDistances)
                    outputValues.Add(double.IsInfinity(v) || double.IsNaN(v) ? 0.0 : v);
            }

            // ── Build SpatialAnalysis ─────────────────────────────────────────
            var analysis = new SpatialAnalysis(resolvedLabel, outputValues);

            // ── Build centers ─────────────────────────────────────────────────
            var centers = new List<Point3d>();
            foreach (var key in orderedKeys)
                centers.Add(voxelGrid.KeyToCenter(key));

            // ── Build gradient ────────────────────────────────────────────────
            // When inverted, the gradient already maps correctly because
            // the output values are already flipped — low distance = high value
            var gradient = ComputeGradient(outputValues);

            // ── Build preview ─────────────────────────────────────────────────
            BuildPreviewData(voxelGrid, outputValues);

            // ── Type summary ──────────────────────────────────────────────────
            var typeCounts = new Dictionary<string, int>();
            foreach (var obj in geometryObjects)
            {
                object inner = obj is Grasshopper.Kernel.Types.GH_ObjectWrapper w
                    ? w.Value : obj;
                string t = inner?.GetType().Name ?? "unknown";
                if (!typeCounts.ContainsKey(t)) typeCounts[t] = 0;
                typeCounts[t]++;
            }

            var typeParts = new List<string>();
            foreach (var kvp in typeCounts)
                typeParts.Add(string.Format("{0}x{1}", kvp.Value, kvp.Key));
            string typeSummary = string.Join(", ", typeParts);

            // ── Stats on output values ────────────────────────────────────────
            double outMin = outputValues.Count > 0 ? outputValues[0] : 0;
            double outMax = outputValues.Count > 0 ? outputValues[0] : 0;

            foreach (var v in outputValues)
            {
                if (v < outMin) outMin = v;
                if (v > outMax) outMax = v;
            }

            string info = string.Format(
                "SA_GeometryDistance | label='{0}' | voxels={1} | " +
                "geom={2} [{3}] | invert={4}\n" +
                "raw_dist=[{5:F4} to {6:F4}] (world units)\n" +
                "output=[{7:F4} to {8:F4}]{9}",
                resolvedLabel, rawDistances.Count,
                geometryObjects.Count, typeSummary,
                invert,
                rawMin, rawMax,
                outMin, outMax,
                nInf > 0
                    ? string.Format(
                        "\nWARNING: {0} voxels returned inf distance", nInf)
                    : "");

            // ── Output ────────────────────────────────────────────────────────
            DA.SetData(0, analysis);
            DA.SetDataList(1, outputValues);
            DA.SetDataList(2, centers);
            DA.SetDataList(3, gradient);
            DA.SetDataList(4, rawDistances);
            DA.SetData(5, info);
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

                // ── GH_Extrusion ──────────────────────────────────────────────
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

                // ── GH_Brep ───────────────────────────────────────────────────
                if (inner is Grasshopper.Kernel.Types.GH_Brep ghBrep)
                {
                    Brep brepVal = ghBrep.Value;
                    if (brepVal != null)
                        return pt.DistanceTo(brepVal.ClosestPoint(pt));
                    return double.PositiveInfinity;
                }

                // ── Native Brep ───────────────────────────────────────────────
                if (inner is Brep nativeBrep)
                    return pt.DistanceTo(nativeBrep.ClosestPoint(pt));

                // ── GH_Surface ────────────────────────────────────────────────
                if (inner is Grasshopper.Kernel.Types.GH_Surface ghSurf)
                {
                    object surfVal = ghSurf.Value;
                    Brep surfBrep = surfVal as Brep;
                    if (surfBrep != null)
                        return pt.DistanceTo(surfBrep.ClosestPoint(pt));
                    return double.PositiveInfinity;
                }

                // ── Native Surface ────────────────────────────────────────────
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
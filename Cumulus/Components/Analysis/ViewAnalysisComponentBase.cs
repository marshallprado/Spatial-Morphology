// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace Cumulus
{
    /// <summary>
    /// Shared implementation for focused voxel-view analysis components.
    /// Derived components choose either spherical or construction-plane-aligned
    /// planar ray distributions.
    /// </summary>
    public abstract class ViewAnalysisComponentBase : SAComponentBase
    {
        private readonly bool _useSphericalDirections;
        private readonly string _defaultLabel;
        private readonly int _defaultSampleCount;

        protected ViewAnalysisComponentBase(
            string name,
            string nickname,
            string description,
            bool useSphericalDirections,
            string defaultLabel,
            int defaultSampleCount)
            : base(
                name,
                nickname,
                description,
                "Cumulus",
                "2 | Analysis")
        {
            _useSphericalDirections = useSphericalDirections;
            _defaultLabel = defaultLabel;
            _defaultSampleCount = defaultSampleCount;
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "voxel_grid",
                "VG",
                "VoxelGrid object from the VoxelGrid component.",
                GH_ParamAccess.item);

            pManager.AddMeshParameter(
                "obstacles",
                "O",
                "Obstacle meshes that block the analysis rays.\n" +
                "Typically the building envelope or urban context.\n" +
                "Optional. Without obstacles, every ray reaches the radius limit.",
                GH_ParamAccess.list);

            pManager.AddIntegerParameter(
                "sample_count",
                "S",
                _useSphericalDirections
                    ? "Number of rays per voxel.\n" +
                      "Spherical View commonly uses 100–500 rays."
                    : "Number of rays per voxel.\n" +
                      "Planar View commonly uses 36–72 rays.",
                GH_ParamAccess.item,
                _defaultSampleCount);

            pManager.AddNumberParameter(
                "radius",
                "R",
                "Maximum ray distance in Rhino model units.\n" +
                "Rays that do not hit an obstacle are clamped to this value.",
                GH_ParamAccess.item,
                1000.0);

            pManager.AddBooleanParameter(
                "invert",
                "I",
                "If true, reverse the values so enclosed space scores high and " +
                "open space scores low.",
                GH_ParamAccess.item,
                false);

            pManager.AddTextParameter(
                "label",
                "L",
                "Optional analysis-channel label used by AnalysisStack.\n" +
                "Leave unconnected to use the default label: " + _defaultLabel + ".",
                GH_ParamAccess.item,
                _defaultLabel);

            pManager[5].Optional = true;

            // Obstacles and Label may be left unconnected.
            pManager[1].Optional = true;
            pManager[5].Optional = true;
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
                "Per-voxel sum of ray distances, inverted when I is true.\n" +
                "Zero indicates below-construction-plane voxels or fully enclosed space.",
                GH_ParamAccess.list);

            pManager.AddPointParameter(
                "centers",
                "C",
                "Voxel centres parallel to V.",
                GH_ParamAccess.list);

            pManager.AddColourParameter(
                "gradient",
                "G",
                "Per-voxel gradient from low (red) to high (blue).",
                GH_ParamAccess.list);

            pManager.AddTextParameter(
                "info",
                "I",
                "Analysis summary.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            object? voxelGridObject = null;
            var obstacleMeshes = new List<Mesh>();
            int sampleCount = _defaultSampleCount;
            double radius = 1000.0;
            bool invert = false;
            string label = _defaultLabel;

            if (!DA.GetData(0, ref voxelGridObject))
                return;

            DA.GetDataList(1, obstacleMeshes);
            DA.GetData(2, ref sampleCount);
            DA.GetData(3, ref radius);
            DA.GetData(4, ref invert);

            string suppliedLabel = string.Empty;

            if (DA.GetData(5, ref suppliedLabel) &&
                !string.IsNullOrWhiteSpace(suppliedLabel))
            {
                label = suppliedLabel.Trim();
            }

            string resolvedLabel = string.IsNullOrWhiteSpace(label)
                ? _defaultLabel
                : label.Trim();

            var voxelGrid = UnwrapVoxelGrid(voxelGridObject);
            if (voxelGrid == null)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Could not read VoxelGrid object.");

                return;
            }

            sampleCount = Math.Max(4, sampleCount);
            radius = Math.Max(1.0, radius);


            Mesh? obstacleMesh = BuildObstacleMesh(obstacleMeshes);

            if (obstacleMesh == null)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "No obstacles connected. Every ray reaches the radius limit, " +
                    "so all above-grade voxels receive the same value.");
            }

            var planeZAxis = new Vector3d(
                voxelGrid.PlaneToWorld.M02,
                voxelGrid.PlaneToWorld.M12,
                voxelGrid.PlaneToWorld.M22);

            if (!planeZAxis.Unitize())
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Could not determine the VoxelGrid construction-plane Z axis.");

                return;
            }

            List<Vector3d> directions = _useSphericalDirections
                ? FibonacciSphere(sampleCount)
                : PlanarCircle(sampleCount, planeZAxis);

            var orderedKeys = voxelGrid.FilledKeys;
            var rawValues = new List<double>(orderedKeys.Count);

            int belowGradeCount = 0;
            int aboveGradeCount = 0;

            foreach (var key in orderedKeys)
            {
                if (voxelGrid.IsBelowGrade(key))
                {
                    rawValues.Add(0.0);
                    belowGradeCount++;
                    continue;
                }

                aboveGradeCount++;

                Point3d origin = voxelGrid.KeyToCenter(key);
                double rayDistanceSum = 0.0;

                foreach (var direction in directions)
                {
                    double distance = radius;

                    if (obstacleMesh != null)
                    {
                        double hitDistance = Intersection.MeshRay(
                            obstacleMesh,
                            new Ray3d(origin, direction));

                        if (hitDistance >= 0.0)
                            distance = Math.Min(hitDistance, radius);
                    }

                    rayDistanceSum += distance;
                }

                rawValues.Add(rayDistanceSum);
            }

            var outputValues = invert
                ? InvertValues(rawValues)
                : rawValues;


            var analysis = new SpatialAnalysis(resolvedLabel, outputValues);

            var centers = new List<Point3d>(orderedKeys.Count);
            foreach (var key in orderedKeys)
                centers.Add(voxelGrid.KeyToCenter(key));

            var gradient = ComputeGradient(outputValues);
            BuildPreviewData(voxelGrid, outputValues);

            GetStatistics(rawValues, out double rawMinimum, out double rawMaximum, out double rawMean);
            GetStatistics(outputValues, out double outputMinimum, out double outputMaximum, out _);

            string distributionName = _useSphericalDirections
                ? "Spherical (3D)"
                : "Planar (2D)";

            string info = string.Format(
                "{0} | label='{1}' | invert={2}\n" +
                "distribution={3} | voxels={4} | rays={5} | radius={6:F1}\n" +
                "above_plane={7} | below_plane={8} (value=0, rays skipped)\n" +
                "max_possible={9:F1} | raw=[{10:F1} to {11:F1}] | raw_mean={12:F1}\n" +
                "output=[{13:F1} to {14:F1}] (sum of ray distances, not normalized)\n" +
                "obstacles={15}",
                Name,
                resolvedLabel,
                invert,
                distributionName,
                rawValues.Count,
                sampleCount,
                radius,
                aboveGradeCount,
                belowGradeCount,
                radius * sampleCount,
                rawMinimum,
                rawMaximum,
                rawMean,
                outputMinimum,
                outputMaximum,
                obstacleMesh != null
                    ? obstacleMeshes.Count + " mesh(es)"
                    : "none");

            DA.SetData(0, analysis);
            DA.SetDataList(1, outputValues);
            DA.SetDataList(2, centers);
            DA.SetDataList(3, gradient);
            DA.SetData(4, info);

            obstacleMesh?.Dispose();
        }

        private static Mesh? BuildObstacleMesh(IEnumerable<Mesh> obstacleMeshes)
        {
            var combined = new Mesh();
            bool containsGeometry = false;

            foreach (var mesh in obstacleMeshes)
            {
                if (mesh == null || !mesh.IsValid || mesh.Vertices.Count == 0)
                    continue;

                combined.Append(mesh);
                containsGeometry = true;
            }

            if (!containsGeometry)
            {
                combined.Dispose();
                return null;
            }

            combined.Compact();
            return combined;
        }

        private static List<Vector3d> FibonacciSphere(int count)
        {
            var directions = new List<Vector3d>(count);

            if (count == 1)
            {
                directions.Add(Vector3d.ZAxis);
                return directions;
            }

            double goldenAngle = Math.PI * (3.0 - Math.Sqrt(5.0));

            for (int index = 0; index < count; index++)
            {
                double y = 1.0 - (index / (double)(count - 1)) * 2.0;
                double radialDistance = Math.Sqrt(Math.Max(0.0, 1.0 - y * y));
                double angle = goldenAngle * index;

                var direction = new Vector3d(
                    Math.Cos(angle) * radialDistance,
                    y,
                    Math.Sin(angle) * radialDistance);

                direction.Unitize();
                directions.Add(direction);
            }

            return directions;
        }

        private static List<Vector3d> PlanarCircle(int count, Vector3d zAxis)
        {
            var directions = new List<Vector3d>(count);

            Vector3d xAxis = Vector3d.CrossProduct(zAxis, Vector3d.ZAxis);
            if (xAxis.Length < 1e-6)
                xAxis = Vector3d.CrossProduct(zAxis, Vector3d.XAxis);

            xAxis.Unitize();

            Vector3d yAxis = Vector3d.CrossProduct(zAxis, xAxis);
            yAxis.Unitize();

            double angleStep = 2.0 * Math.PI / count;

            for (int index = 0; index < count; index++)
            {
                double angle = index * angleStep;
                double cosine = Math.Cos(angle);
                double sine = Math.Sin(angle);

                var direction = new Vector3d(
                    cosine * xAxis.X + sine * yAxis.X,
                    cosine * xAxis.Y + sine * yAxis.Y,
                    cosine * xAxis.Z + sine * yAxis.Z);

                direction.Unitize();
                directions.Add(direction);
            }

            return directions;
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
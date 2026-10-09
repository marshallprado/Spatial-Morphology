// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace Cumulus
{
    /// <summary>
    /// Shared implementation for focused surface-shell distance analysis components.
    /// It performs one breadth-first traversal from the VoxelGrid surface shell.
    /// </summary>
    public abstract class SurfaceShellDistanceComponentBase : SAComponentBase
    {
        private readonly bool _useMetricDistance;
        private readonly string _defaultLabel;
        protected SurfaceShellDistanceComponentBase(
           string name,
           string nickname,
           string description,
           bool useMetricDistance,
           string defaultLabel)
           : base(
               name,
               nickname,
               description,
               "Cumulus",
               "2 | Analysis")
        {
            _useMetricDistance = useMetricDistance;
            _defaultLabel = defaultLabel;
        }

        // Existing RegisterInputParams, RegisterOutputParams,
        // and SolveInstance code follows.


        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "voxel_grid",
                "VG",
                "VoxelGrid object from the VoxelGrid component.",
                GH_ParamAccess.item);

            pManager.AddBooleanParameter(
                "invert",
                "I",
                "If true, reverse values so surface voxels score high and interior voxels score low.",
                GH_ParamAccess.item,
                false);

            pManager.AddTextParameter(
                "label",
                "L",
                "Analysis-channel label used by AnalysisStack. Leave blank to use the component default.",
                GH_ParamAccess.item,
                string.Empty);
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
                "Per-voxel surface-shell distance, inverted when I is true.",
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

            pManager.AddTextParameter(
                "info",
                "I",
                "Analysis summary.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            object? voxelGridObject = null;
            bool invert = false;
            string label = string.Empty;

            if (!DA.GetData(0, ref voxelGridObject))
                return;

            DA.GetData(1, ref invert);
            DA.GetData(2, ref label);

            VoxelGrid? voxelGrid = UnwrapVoxelGrid(voxelGridObject!);
            if (voxelGrid == null)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Could not read a VoxelGrid object from VG.");
                return;
            }

            if (voxelGrid.SurfaceKeys.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "No surface voxels were found. The voxelized mesh may not be closed.");
                return;
            }

            SurfaceShellDistances distances = CalculateDistances(voxelGrid);

            var rawValues = new List<double>(voxelGrid.FilledKeys.Count);
            foreach (var key in voxelGrid.FilledKeys)
            {
                rawValues.Add(_useMetricDistance
                    ? distances.Metric[key]
                    : distances.Hops[key]);
            }

            List<double> outputValues = invert ? InvertValues(rawValues) : rawValues;
            string resolvedLabel = string.IsNullOrWhiteSpace(label)
                ? _defaultLabel
                : label.Trim();

            var centers = new List<Point3d>(voxelGrid.FilledKeys.Count);
            foreach (var key in voxelGrid.FilledKeys)
                centers.Add(voxelGrid.KeyToCenter(key));

            DA.SetData(0, new SpatialAnalysis(resolvedLabel, outputValues));
            DA.SetDataList(1, outputValues);
            DA.SetDataList(2, centers);
            DA.SetDataList(3, ComputeGradient(outputValues));
            DA.SetData(4, BuildInfo(
                resolvedLabel,
                invert,
                rawValues,
                outputValues,
                distances.MaximumHop,
                voxelGrid.SurfaceKeys.Count,
                distances.DisconnectedCount));

            BuildPreviewData(voxelGrid, outputValues);
        }

        private string BuildInfo(
            string label,
            bool invert,
            List<double> raw,
            List<double> output,
            int maximumHop,
            int surfaceVoxelCount,
            int disconnectedCount)
        {
            double rawMin;
            double rawMax;
            GetRange(raw, out rawMin, out rawMax);

            double outputMin;
            double outputMax;
            GetRange(output, out outputMin, out outputMax);

            string measureName = _useMetricDistance
                ? "Surface Metric Distance"
                : "Surface Depth";
            string unit = _useMetricDistance ? "model units" : "hops";
            string format = _useMetricDistance ? "F4" : "F0";

            return string.Format(
                "{0} | label='{1}' | voxels={2} | invert={3}\n" +
                "raw=[{4} to {5}] | output=[{6} to {7}] ({8}, not normalized)\n" +
                "bfs_layers={9} | surface_voxels={10}{11}",
                measureName,
                label,
                raw.Count,
                invert,
                rawMin.ToString(format),
                rawMax.ToString(format),
                outputMin.ToString(format),
                outputMax.ToString(format),
                unit,
                maximumHop,
                surfaceVoxelCount,
                disconnectedCount > 0
                    ? string.Format(
                        "\nWARNING: {0} disconnected voxel(s) were set past the maximum.",
                        disconnectedCount)
                    : string.Empty);
        }

        private static void GetRange(
            IReadOnlyList<double> values,
            out double minimum,
            out double maximum)
        {
            minimum = values.Count == 0 ? 0.0 : values[0];
            maximum = minimum;

            for (int index = 1; index < values.Count; index++)
            {
                if (values[index] < minimum)
                    minimum = values[index];
                if (values[index] > maximum)
                    maximum = values[index];
            }
        }

        private static SurfaceShellDistances CalculateDistances(VoxelGrid voxelGrid)
        {
            var hops = new Dictionary<(int, int, int), int>();
            var metric = new Dictionary<(int, int, int), double>();
            var seedCenters = new Dictionary<(int, int, int), Point3d>();
            var queue = new Queue<(int, int, int)>();

            foreach (var key in voxelGrid.SurfaceKeys)
            {
                Point3d center = voxelGrid.KeyToCenter(key);
                hops[key] = 0;
                metric[key] = 0.0;
                seedCenters[key] = center;
                queue.Enqueue(key);
            }

            var filled = voxelGrid.FilledKeysSet;
            int[] dx = { 1, -1, 0, 0, 0, 0 };
            int[] dy = { 0, 0, 1, -1, 0, 0 };
            int[] dz = { 0, 0, 0, 0, 1, -1 };

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                int currentHop = hops[current];
                Point3d seedCenter = seedCenters[current];

                for (int direction = 0; direction < 6; direction++)
                {
                    var next = (
                        current.Item1 + dx[direction],
                        current.Item2 + dy[direction],
                        current.Item3 + dz[direction]);

                    if (!filled.Contains(next) || hops.ContainsKey(next))
                        continue;

                    hops[next] = currentHop + 1;
                    metric[next] = voxelGrid.KeyToCenter(next).DistanceTo(seedCenter);
                    seedCenters[next] = seedCenter;
                    queue.Enqueue(next);
                }
            }

            int maximumHop = 0;
            double maximumMetric = 0.0;

            foreach (int hop in hops.Values)
                maximumHop = Math.Max(maximumHop, hop);

            foreach (double value in metric.Values)
                maximumMetric = Math.Max(maximumMetric, value);

            int disconnectedCount = 0;
            foreach (var key in filled)
            {
                if (hops.ContainsKey(key))
                    continue;

                hops[key] = maximumHop + 1;
                metric[key] = maximumMetric + voxelGrid.VoxelSize;
                disconnectedCount++;
            }

            return new SurfaceShellDistances(
                hops,
                metric,
                maximumHop,
                disconnectedCount);
        }

        private sealed class SurfaceShellDistances
        {
            public Dictionary<(int, int, int), int> Hops { get; }
            public Dictionary<(int, int, int), double> Metric { get; }
            public int MaximumHop { get; }
            public int DisconnectedCount { get; }

            public SurfaceShellDistances(
                Dictionary<(int, int, int), int> hops,
                Dictionary<(int, int, int), double> metric,
                int maximumHop,
                int disconnectedCount)
            {
                Hops = hops;
                Metric = metric;
                MaximumHop = maximumHop;
                DisconnectedCount = disconnectedCount;
            }
        }
    }
}

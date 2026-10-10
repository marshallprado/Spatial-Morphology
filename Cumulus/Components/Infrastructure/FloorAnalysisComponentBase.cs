// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace Cumulus
{
    /// <summary>
    /// Shared implementation for focused floor-based analysis components.
    /// This abstract type is not exposed as a Grasshopper component.
    /// </summary>
    public abstract class FloorAnalysisComponentBase : SAComponentBase
    {
        protected enum FloorAnalysisKind
        {
            FloorIndex,
            Walkup,
            Commercial,
            RealEstate
        }

        private readonly FloorAnalysisKind _kind;
        private readonly string _defaultLabel;
        private readonly string _analysisName;

        protected FloorAnalysisComponentBase(
            string name,
            string nickname,
            string description,
            FloorAnalysisKind kind,
            string defaultLabel)
            : base(name, nickname, description, "Cumulus", "2 | Analysis")
        {
            _kind = kind;
            _defaultLabel = defaultLabel;
            _analysisName = name;
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "voxel_grid",
                "VG",
                "VoxelGrid object from the VoxelGrid component.",
                GH_ParamAccess.item);

            pManager.AddNumberParameter(
                "floor_height",
                "H",
                "Height of one floor in Rhino model units. It determines the " +
                "floor boundaries relative to the VoxelGrid construction plane.",
                GH_ParamAccess.item,
                10.0);

            pManager.AddBooleanParameter(
                "invert",
                "I",
                "If true, reverse the calculated output-value range. Floor indices " +
                "are never inverted.",
                GH_ParamAccess.item,
                false);

            pManager.AddTextParameter(
                "label",
                "L",
                "Channel name used by AnalysisStack. Default: '" +
                    _defaultLabel + "'.",
                GH_ParamAccess.item,
                _defaultLabel);

            // L is optional. If it is not wired, SolveInstance uses _defaultLabel.
            pManager[3].Optional = true;
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
                "Per-voxel floor analysis values, inverted when I is true.",
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

            pManager.AddIntegerParameter(
                "floor_indices",
                "F",
                "Per-voxel raw floor number. This is never inverted or remapped.",
                GH_ParamAccess.list);

            pManager.AddTextParameter(
                "info",
                "I",
                "Summary and floor distribution.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            object voxelGridObject = null;
            double floorHeight = 10.0;
            bool invert = false;
            string label = _defaultLabel;

            if (!DA.GetData(0, ref voxelGridObject))
                return;

            DA.GetData(1, ref floorHeight);
            DA.GetData(2, ref invert);

            // L is optional. Leave label as _defaultLabel when no value is supplied.
            string suppliedLabel = string.Empty;
            if (DA.GetData(3, ref suppliedLabel) &&
                !string.IsNullOrWhiteSpace(suppliedLabel))
            {
                label = suppliedLabel.Trim();
            }

            var voxelGrid = UnwrapVoxelGrid(voxelGridObject);
            if (voxelGrid == null)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Could not read VoxelGrid object.");
                return;
            }

            if (floorHeight <= 0.0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Floor height must be greater than zero.");
                return;
            }

            string resolvedLabel = string.IsNullOrWhiteSpace(label)
                ? _defaultLabel
                : label.Trim();

            var planeOrigin = new Point3d(
                voxelGrid.PlaneToWorld.M03,
                voxelGrid.PlaneToWorld.M13,
                voxelGrid.PlaneToWorld.M23);

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

            var keys = voxelGrid.FilledKeys;
            var floorIndices = new List<int>(keys.Count);
            var floorCounts = new Dictionary<int, int>();
            var centers = new List<Point3d>(keys.Count);

            foreach (var key in keys)
            {
                Point3d center = voxelGrid.KeyToCenter(key);
                centers.Add(center);

                Vector3d offset = center - planeOrigin;
                double heightAbovePlane = Vector3d.Multiply(offset, planeZAxis);
                int floorIndex = ComputeFloorIndex(heightAbovePlane, floorHeight);

                floorIndices.Add(floorIndex);

                int count;
                floorCounts.TryGetValue(floorIndex, out count);
                floorCounts[floorIndex] = count + 1;
            }

            int topFloor = 1;
            foreach (int floor in floorIndices)
                if (floor > topFloor)
                    topFloor = floor;

            int penthouseStart = -1;
            bool penthouseBound = false;
            Dictionary<int, double> realEstateTable = null;

            if (_kind == FloorAnalysisKind.RealEstate)
            {
                realEstateTable = BuildRealEstateTable(
                    topFloor,
                    out penthouseStart,
                    out penthouseBound);
            }

            var rawValues = new List<double>(floorIndices.Count);
            foreach (int floorIndex in floorIndices)
                rawValues.Add(ValueForFloor(floorIndex, realEstateTable));

            // The existing Real Estate mode normalizes before inversion.
            if (_kind == FloorAnalysisKind.RealEstate)
                NormalizeInPlace(rawValues);

            var outputValues = invert
                ? InvertValues(rawValues)
                : rawValues;

            var analysis = new SpatialAnalysis(resolvedLabel, outputValues);
            var gradient = ComputeGradient(outputValues);
            BuildPreviewData(voxelGrid, outputValues);

            GetRange(rawValues, out double rawMinimum, out double rawMaximum);
            GetRange(outputValues, out double outputMinimum, out double outputMaximum);

            string floorDistribution = BuildFloorDistribution(
                floorCounts,
                floorHeight,
                realEstateTable,
                penthouseStart);

            string realEstateNote = string.Empty;
            if (_kind == FloorAnalysisKind.RealEstate)
            {
                int penthouseCount = penthouseStart > 0
                    ? Math.Max(0, topFloor - penthouseStart + 1)
                    : 0;

                realEstateNote = string.Format(
                    "\npenthouse_floors={0} (from floor {1}) | override_bound={2}",
                    penthouseCount,
                    penthouseStart > 0 ? penthouseStart.ToString() : "n/a",
                    penthouseBound
                        ? "yes — lifted to 35% above average"
                        : "no — growth curve already exceeded it");
            }

            string info = string.Format(
                "{0} | label='{1}' | invert={2}\n" +
                "voxels={3} | floor_height={4:F2} | distinct_floors={5} | top_floor={6}\n" +
                "raw=[{7:F4} to {8:F4}] | output=[{9:F4} to {10:F4}]{11}\n\n" +
                "Floor distribution:\n{12}",
                _analysisName,
                resolvedLabel,
                invert,
                keys.Count,
                floorHeight,
                floorCounts.Count,
                topFloor,
                rawMinimum,
                rawMaximum,
                outputMinimum,
                outputMaximum,
                realEstateNote,
                floorDistribution);

            DA.SetData(0, analysis);
            DA.SetDataList(1, outputValues);
            DA.SetDataList(2, centers);
            DA.SetDataList(3, gradient);
            DA.SetDataList(4, floorIndices);
            DA.SetData(5, info);
        }

        private double ValueForFloor(
            int floorIndex,
            Dictionary<int, double> realEstateTable)
        {
            switch (_kind)
            {
                case FloorAnalysisKind.FloorIndex:
                    return floorIndex;

                case FloorAnalysisKind.Walkup:
                    return WalkupValue(floorIndex);

                case FloorAnalysisKind.Commercial:
                    return CommercialValue(floorIndex);

                case FloorAnalysisKind.RealEstate:
                    if (realEstateTable != null &&
                        realEstateTable.TryGetValue(floorIndex, out double value))
                    {
                        return value;
                    }

                    return 0.0;

                default:
                    return 0.0;
            }
        }

        /// <summary>
        /// There is no floor zero. Heights on or above the construction plane
        /// map to floor one and above; negative heights map to floor minus one
        /// and below.
        /// </summary>
        protected static int ComputeFloorIndex(
            double heightAbovePlane,
            double floorHeight)
        {
            int rawFloor = (int)Math.Floor(heightAbovePlane / floorHeight);
            return rawFloor >= 0 ? rawFloor + 1 : rawFloor;
        }

        private static double WalkupValue(int floorIndex)
        {
            int travelDistance = Math.Abs(floorIndex - 1);

            if (travelDistance >= 7)
                return 0.0;

            return Math.Max(1.0, 5.0 - travelDistance);
        }

        private static double CommercialValue(int floorIndex)
        {
            if (floorIndex <= 0)
                return 0.0;

            return Math.Max(0.0, 5.0 - (floorIndex - 1));
        }

        private static double GrowthFactor(int floor)
        {
            if (floor <= 5) return 1.010;
            if (floor == 6) return 0.990;
            if (floor == 7) return 1.030;
            if (floor <= 9) return 1.015;
            if (floor == 10) return 1.050;
            if (floor <= 24) return 1.015;
            if (floor <= 29) return 1.050;
            return 1.015;
        }

        private static Dictionary<int, double> BuildRealEstateTable(
            int maximumFloor,
            out int penthouseStart,
            out bool penthouseBound)
        {
            var table = new Dictionary<int, double>();
            double previous = 0.0;

            for (int floor = 1; floor <= maximumFloor; floor++)
            {
                double value = floor == 1
                    ? 1.0
                    : previous * GrowthFactor(floor);

                table[floor] = value;
                previous = value;
            }

            penthouseStart = maximumFloor - (int)Math.Floor(maximumFloor * 0.06);
            penthouseStart = Math.Max(2, penthouseStart);
            penthouseBound = false;

            if (penthouseStart > maximumFloor)
                return table;

            double sum = 0.0;
            int count = 0;

            for (int floor = 1; floor < penthouseStart; floor++)
            {
                if (table.TryGetValue(floor, out double value))
                {
                    sum += value;
                    count++;
                }
            }

            double average = count > 0 ? sum / count : 1.0;
            double penthouseMinimum = average * 1.35;
            double previousPenthouseValue = 0.0;

            for (int floor = penthouseStart; floor <= maximumFloor; floor++)
            {
                double value;

                if (floor == penthouseStart)
                {
                    double growthCurveValue = table[floor];
                    value = Math.Max(growthCurveValue, penthouseMinimum);
                    penthouseBound = penthouseMinimum > growthCurveValue;
                }
                else
                {
                    value = previousPenthouseValue * 1.02;
                }

                table[floor] = value;
                previousPenthouseValue = value;
            }

            return table;
        }

        private static void NormalizeInPlace(List<double> values)
        {
            if (values.Count == 0)
                return;

            GetRange(values, out double minimum, out double maximum);
            double span = maximum - minimum;

            for (int index = 0; index < values.Count; index++)
            {
                values[index] = span > 1e-12
                    ? (values[index] - minimum) / span
                    : 0.0;
            }
        }

        private static void GetRange(
            IReadOnlyList<double> values,
            out double minimum,
            out double maximum)
        {
            if (values.Count == 0)
            {
                minimum = 0.0;
                maximum = 0.0;
                return;
            }

            minimum = values[0];
            maximum = values[0];

            foreach (double value in values)
            {
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
            }
        }

        private string BuildFloorDistribution(
            Dictionary<int, int> floorCounts,
            double floorHeight,
            Dictionary<int, double> realEstateTable,
            int penthouseStart)
        {
            var floors = new List<int>(floorCounts.Keys);
            floors.Sort();

            var lines = new System.Text.StringBuilder();

            foreach (int floor in floors)
            {
                double lowerHeight = floor > 0
                    ? (floor - 1) * floorHeight
                    : floor * floorHeight;

                double upperHeight = floor > 0
                    ? floor * floorHeight
                    : (floor + 1) * floorHeight;

                bool isPenthouse =
                    _kind == FloorAnalysisKind.RealEstate &&
                    penthouseStart > 0 &&
                    floor >= penthouseStart;

                lines.AppendLine(string.Format(
                    "  Floor {0,4} | {1,9:F1} to {2,9:F1} | value={3,9:F4} | voxels={4,6}{5}",
                    floor,
                    lowerHeight,
                    upperHeight,
                    ValueForFloor(floor, realEstateTable),
                    floorCounts[floor],
                    isPenthouse ? " | penthouse" : string.Empty));
            }

            return lines.ToString().TrimEnd();
        }
    }
}

// -*- coding: utf-8 -*-
// Version 4.4.0
using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace SpatialMorphology
{
    /// <summary>
    /// Assigns each voxel a value derived from its floor level relative to the
    /// VoxelGrid construction plane.
    /// </summary>
    /// <remarks>
    /// RENAMED in 4.4.0 — was SA_FloorLevel / SA_Floor. The ComponentGuid is
    /// deliberately UNCHANGED so saved definitions still resolve to this class
    /// rather than appearing as unrecognised objects.
    ///
    /// The default label is unchanged at "floor_level", so no ValueSet matrix
    /// needs re-confirming after this rename.
    ///
    /// MODE LOGIC IS UNCHANGED from 4.3.0. The floor numbering, the four mode
    /// curves, the mode 3 growth schedule, the penthouse override and the
    /// normalise-then-invert order all behave exactly as before.
    ///
    /// FLOOR NUMBERING — there is no floor 0. Heights at or above the plane map
    /// to +1, +2, ... and heights below map to -1, -2, ... So floor 1 is the
    /// first storey above grade and floor -1 is the first below it. The floor
    /// distribution table in the info output prints the height band per floor
    /// so this is visible rather than inferred.
    /// </remarks>
    public class FloorLevelComponent : SAComponentBase
    {
        // ── Constructor ───────────────────────────────────────────────────────
        public FloorLevelComponent()
            : base(
                "Floor Level",
                "Floor",
                "Assigns each voxel a value based on its floor level relative\n" +
                "to the VoxelGrid construction plane.\n\n" +
                "There is no floor 0 — floor 1 is the first storey above the\n" +
                "plane, floor -1 the first below it.\n\n" +
                "Mode 0 — Floor Index:\n" +
                "  The raw floor integer. Positive above the plane, negative\n" +
                "  below. Use when you want the storey number itself.\n\n" +
                "Mode 1 — Walkup:\n" +
                "  Street level scores 5, dropping 1 per floor of travel away\n" +
                "  from the street in EITHER direction. Floors 4-6 away are\n" +
                "  held at 1. Floors 7+ away score 0.\n" +
                "  Models the cost of stairs when there is no lift.\n\n" +
                "Mode 2 — Commercial:\n" +
                "  Street level scores highest, stepping down 1 per floor\n" +
                "  going up, floored at 0. Everything below street scores 0.\n" +
                "  Models retail footfall.\n\n" +
                "Mode 3 — Real Estate:\n" +
                "  A progressive market-value curve. Floor 1 is the base and\n" +
                "  each floor above compounds by a per-band growth rate, with\n" +
                "  premium jumps at floors 7, 10 and 25-29.\n" +
                "  The top 6% of floors are treated as penthouse and lifted to\n" +
                "  at least 35% above the average of the floors below them,\n" +
                "  then +2% per floor after that.\n" +
                "  Below street scores 0. Output is normalised to 0..1.\n\n" +
                "invert = False (default): the calculated values pass through.\n" +
                "invert = True: the value range is reversed, so whatever the\n" +
                "  mode rated highest becomes lowest.\n" +
                "  In mode 3 this is applied AFTER normalisation.\n\n" +
                "Version 4.4.0",
                "Spatial Morphology",
                "2 | Analysis")
        { }

        // ── Ribbon placement ──────────────────────────────────────────────────
        // Voxel-internal analysis — reads only the construction plane.
        public override GH_Exposure Exposure => GH_Exposure.secondary;

        // ── GUID — DO NOT CHANGE ──────────────────────────────────────────────
        // Kept from the SA_FloorLevel era so old .gh files keep working.
        public override Guid ComponentGuid =>
            new Guid("C3D4E5F6-A7B8-9012-CDEF-012345678910");

        // ── Icon ──────────────────────────────────────────────────────────────
        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "SpatialMorphology.Resources.FloorLevel_24.png");
                return stream != null ? new Bitmap(stream) : null;
            }
        }

        // ── Mode names ────────────────────────────────────────────────────────
        private static readonly string[] MODE_NAMES =
        {
            "Floor Index",
            "Walkup",
            "Commercial",
            "Real Estate (normalized)"
        };

        // ── Parameters ────────────────────────────────────────────────────────
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("voxel_grid", "VG",
                "VoxelGrid object from the VoxelGrid component.",
                GH_ParamAccess.item);
            pManager.AddNumberParameter("height", "H",
                "Height of one floor in model units.\n" +
                "Default: 10.0. Set this to match your model units — it\n" +
                "determines where the floor boundaries fall.",
                GH_ParamAccess.item, 10.0);
            pManager.AddIntegerParameter("mode", "M",
                "Analysis mode:\n" +
                "  0 = Floor Index  (raw integer, positive up / negative down)\n" +
                "  1 = Walkup       (5 at street, decreasing away, 0 at 7+)\n" +
                "  2 = Commercial   (highest at street, steps down going up)\n" +
                "  3 = Real Estate  (progressive market curve, normalised)",
                GH_ParamAccess.item, 0);
            pManager.AddBooleanParameter("invert", "I",
                "If True, reverse the value range so low becomes high and high\n" +
                "becomes low. The gradient and voxel preview reverse to match.\n" +
                "In mode 3 this is applied after normalisation.\n" +
                "Default: false.",
                GH_ParamAccess.item, false);
            pManager.AddTextParameter("label", "L",
                "Channel name used by AnalysisStack. Default: 'floor_level'.",
                GH_ParamAccess.item, "floor_level");
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("analysis", "A",
                "SpatialAnalysis object. Wire into AnalysisStack.",
                GH_ParamAccess.item);
            pManager.AddNumberParameter("values", "V",
                "Per-voxel value for the selected mode, inverted if Invert is\n" +
                "true.",
                GH_ParamAccess.list);
            pManager.AddPointParameter("centers", "C",
                "Voxel centres for preview.",
                GH_ParamAccess.list);
            pManager.AddColourParameter("gradient", "G",
                "Per-voxel gradient color from low (red) to high (blue),\n" +
                "based on the output values.",
                GH_ParamAccess.list);
            pManager.AddIntegerParameter("floor_indices", "F",
                "Per-voxel floor number. Never inverted, never remapped —\n" +
                "the raw storey index regardless of mode.",
                GH_ParamAccess.list);
            pManager.AddTextParameter("info", "I",
                "Summary including the floor distribution table.",
                GH_ParamAccess.item);
        }

        // ── Solve ─────────────────────────────────────────────────────────────
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            object voxelGridObj = null;
            double height = 10.0;
            int mode = 0;
            bool invert = false;
            string label = "floor_level";

            if (!DA.GetData(0, ref voxelGridObj)) return;
            DA.GetData(1, ref height);
            DA.GetData(2, ref mode);
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
                ? "floor_level" : label.Trim();

            if (height <= 0.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "height must be greater than 0.");
                return;
            }

            mode = Math.Max(0, Math.Min(3, mode));

            // ── Construction plane origin and up axis ─────────────────────────
            var planeOrigin = new Point3d(
                voxelGrid.PlaneToWorld.M03,
                voxelGrid.PlaneToWorld.M13,
                voxelGrid.PlaneToWorld.M23);

            var planeZAxis = new Vector3d(
                voxelGrid.PlaneToWorld.M02,
                voxelGrid.PlaneToWorld.M12,
                voxelGrid.PlaneToWorld.M22);
            planeZAxis.Unitize();

            // ── Floor level per voxel ─────────────────────────────────────────
            var orderedKeys = voxelGrid.FilledKeys;
            var floorLevels = new List<int>(orderedKeys.Count);
            var floorCounts = new Dictionary<int, int>();

            foreach (var key in orderedKeys)
            {
                Point3d center = voxelGrid.KeyToCenter(key);
                Vector3d offset = center - planeOrigin;
                double h = Vector3d.Multiply(offset, planeZAxis);

                int floorLevel = ComputeFloorLevel(h, height);
                floorLevels.Add(floorLevel);

                if (!floorCounts.ContainsKey(floorLevel)) floorCounts[floorLevel] = 0;
                floorCounts[floorLevel]++;
            }

            int maxFloor = 1;
            foreach (var fl in floorLevels)
                if (fl > maxFloor) maxFloor = fl;

            // ── Mode 3 lookup table, built once ───────────────────────────────
            int penthouseStart = -1;
            bool penthouseBound = false;

            Dictionary<int, double> mode3Table = null;
            if (mode == 3)
                mode3Table = BuildMode3Table(
                    maxFloor, out penthouseStart, out penthouseBound);

            // ── Raw values ────────────────────────────────────────────────────
            var raw = new List<double>(floorLevels.Count);
            foreach (var fl in floorLevels)
                raw.Add(ValueForFloor(fl, mode, mode3Table));

            // ── Mode 3 normalises to 0..1 BEFORE any inversion ────────────────
            if (mode == 3 && raw.Count > 0)
            {
                double lo = raw[0], hi = raw[0];
                foreach (var value in raw)
                {
                    if (value < lo) lo = value;
                    if (value > hi) hi = value;
                }

                double span = hi - lo;
                for (int i = 0; i < raw.Count; i++)
                    raw[i] = span > 1e-12 ? (raw[i] - lo) / span : 0.0;
            }

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

            // ── Floor distribution table ──────────────────────────────────────
            var sortedFloors = new List<int>(floorCounts.Keys);
            sortedFloors.Sort();

            var floorLines = new System.Text.StringBuilder();
            foreach (var fl in sortedFloors)
            {
                // Floor 1 spans 0..height, floor -1 spans -height..0.
                double loH = fl > 0 ? (fl - 1) * height : fl * height;
                double hiH = fl > 0 ? fl * height : (fl + 1) * height;

                bool isPenthouse = mode == 3
                    && penthouseStart > 0
                    && fl >= penthouseStart;

                floorLines.AppendLine(string.Format(
                    "  Floor {0,4} | {1,9:F1} to {2,9:F1} | value={3,9:F4} | voxels={4,6}{5}",
                    fl, loH, hiH,
                    ValueForFloor(fl, mode, mode3Table),
                    floorCounts[fl],
                    isPenthouse ? " | penthouse" : ""));
            }

            string penthouseNote = "";
            if (mode == 3)
            {
                int nPenthouse = penthouseStart > 0
                    ? Math.Max(0, maxFloor - penthouseStart + 1) : 0;

                penthouseNote = string.Format(
                    "\npenthouse_floors={0} (from floor {1}) | override_bound={2}",
                    nPenthouse,
                    penthouseStart > 0 ? penthouseStart.ToString() : "n/a",
                    penthouseBound
                        ? "yes — lifted to 35% above average"
                        : "no — growth curve already exceeded it");
            }

            string info = string.Format(
                "Floor Level | label='{0}' | mode={1} ({2}) | invert={3}\n" +
                "voxels={4} | floor_height={5:F2} | distinct_floors={6} | top_floor={7}\n" +
                "raw=[{8:F4} to {9:F4}] | output=[{10:F4} to {11:F4}]{12}\n\n" +
                "Floor distribution:\n{13}",
                resolvedLabel, mode, MODE_NAMES[mode], invert,
                raw.Count, height, floorCounts.Count, maxFloor,
                rawMin, rawMax, outputMin, outputMax,
                penthouseNote,
                floorLines.ToString().TrimEnd());

            DA.SetData(0, analysis);
            DA.SetDataList(1, outputValues);
            DA.SetDataList(2, centers);
            DA.SetDataList(3, gradient);
            DA.SetDataList(4, floorLevels);
            DA.SetData(5, info);
        }

        // ── Mode dispatch ─────────────────────────────────────────────────────
        private static double ValueForFloor(
            int floorLevel, int mode, Dictionary<int, double> mode3Table)
        {
            switch (mode)
            {
                case 0: return ComputeMode0(floorLevel);
                case 1: return ComputeMode1Walkup(floorLevel);
                case 2: return ComputeMode2Commercial(floorLevel);
                case 3:
                    return mode3Table != null && mode3Table.ContainsKey(floorLevel)
                        ? mode3Table[floorLevel] : 0.0;
                default: return 0.0;
            }
        }

        // ── Floor level from height above the plane ───────────────────────────
        // No floor 0: at or above the plane gives +1, +2, ...
        //             below the plane gives -1, -2, ...
        private static int ComputeFloorLevel(
            double heightAbovePlane, double floorHeight)
        {
            int rawFloor = (int)Math.Floor(heightAbovePlane / floorHeight);
            return rawFloor >= 0 ? rawFloor + 1 : rawFloor;
        }

        // ── Mode 0 — Floor Index ──────────────────────────────────────────────
        private static double ComputeMode0(int floorLevel)
        {
            return floorLevel;
        }

        // ── Mode 1 — Walkup ──────────────────────────────────────────────────
        // Symmetric around street level: stairs cost the same up or down.
        private static double ComputeMode1Walkup(int floorLevel)
        {
            int dist = Math.Abs(floorLevel - 1);
            if (dist >= 7) return 0.0;
            return Math.Max(1.0, 5.0 - dist);
        }

        // ── Mode 2 — Commercial ──────────────────────────────────────────────
        // Retail footfall: street is best, below street is worthless.
        private static double ComputeMode2Commercial(int floorLevel)
        {
            if (floorLevel <= 0) return 0.0;
            return Math.Max(0.0, 5.0 - (floorLevel - 1));
        }

        // ── Mode 3 — per-floor compounding growth rate ────────────────────────
        // Factor applied to the floor below. Premium jumps at 7, 10 and 25-29
        // reflect the points where view and prestige step up in practice.
        private static double GrowthFactor(int floor)
        {
            if (floor <= 5)  return 1.010;   // floors 2-5
            if (floor == 6)  return 0.990;   // floor 6 dips
            if (floor == 7)  return 1.030;   // first view premium
            if (floor <= 9)  return 1.015;   // floors 8-9
            if (floor == 10) return 1.050;   // second view premium
            if (floor <= 24) return 1.015;   // floors 11-24
            if (floor <= 29) return 1.050;   // floors 25-29 premium band
            return 1.015;                    // floors 30+
        }

        // ── Mode 3 — Real Estate curve table ─────────────────────────────────
        // Returns floor -> unnormalised value for floors 1..maxFloor.
        // Below-grade floors are simply absent, so they resolve to 0.0.
        private static Dictionary<int, double> BuildMode3Table(
            int maxFloor, out int penthouseStart, out bool penthouseBound)
        {
            var table = new Dictionary<int, double>();

            // ── Compounding growth from a base of 1.0 at floor 1 ──────────────
            double prev = 0.0;
            for (int f = 1; f <= maxFloor; f++)
            {
                double current = f == 1 ? 1.0 : prev * GrowthFactor(f);
                table[f] = current;
                prev = current;
            }

            // ── Penthouse band — top 6% of floors, minimum 1 floor ───────────
            penthouseStart = maxFloor - (int)Math.Floor(maxFloor * 0.06);
            penthouseStart = Math.Max(2, penthouseStart);
            penthouseBound = false;

            if (penthouseStart > maxFloor) return table;

            // Average of every floor BELOW the penthouse band.
            double sum = 0.0;
            int count = 0;
            for (int f = 1; f < penthouseStart; f++)
            {
                if (!table.ContainsKey(f)) continue;
                sum += table[f];
                count++;
            }

            double avgValue = count > 0 ? sum / count : 1.0;
            double penthouseMin = avgValue * 1.35;

            // First penthouse floor is lifted to penthouseMin if the growth
            // curve has not already passed it. In a tall stack it usually has,
            // in which case the override does nothing — penthouseBound records
            // which of the two happened.
            double penthousePrev = 0.0;
            for (int f = penthouseStart; f <= maxFloor; f++)
            {
                double value;

                if (f == penthouseStart)
                {
                    double curveValue = table.ContainsKey(f) ? table[f] : 0.0;
                    value = Math.Max(curveValue, penthouseMin);
                    penthouseBound = penthouseMin > curveValue;
                }
                else
                {
                    value = penthousePrev * 1.02;
                }

                table[f] = value;
                penthousePrev = value;
            }

            return table;
        }
    }
}

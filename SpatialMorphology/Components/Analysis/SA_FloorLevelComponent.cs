// -*- coding: utf-8 -*-
// Version 4.2.0
using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace SpatialMorphology
{
    public class SA_FloorLevelComponent : SAComponentBase
    {
        // ── Constructor ───────────────────────────────────────────────────────
        public SA_FloorLevelComponent()
            : base(
                "SA_FloorLevel",
                "SA_Floor",
                "Assigns each voxel a performance value based on its floor level\n" +
                "relative to the VoxelGrid construction plane.\n\n" +
                "Mode 0 — Floor Index:\n" +
                "  Raw floor integer. Positive above plane, negative below.\n\n" +
                "Mode 1 — Walkup:\n" +
                "  Street level = 5. Decreases 1 per floor away from street.\n" +
                "  Capped at 1 for floors 4-6 away. 0 for 7+ floors away.\n\n" +
                "Mode 2 — Commercial:\n" +
                "  Below street = 0. Street level = highest.\n" +
                "  Steps down 1 per floor above street. Min = 0.\n\n" +
                "Mode 3 — Real Estate:\n" +
                "  Ground floor = 1.0. Progressive increases per floor.\n" +
                "  Penthouse = top 6% of floors, at least 35% above average.\n" +
                "  Below street = 0. Normalized to [0,1].\n\n" +
                "Version 4.2.0",
                "Spatial Morphology",
                "Analysis")
        { }

        // ── GUID ──────────────────────────────────────────────────────────────
        public override Guid ComponentGuid =>
            new Guid("C3D4E5F6-A7B8-9012-CDEF-012345678910");

        // ── Icon ──────────────────────────────────────────────────────────────
        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "SpatialMorphology.Resources.SA_FloorLevel_24.png");
                return stream != null ? new Bitmap(stream) : null;
            }
        }

        // ── Parameters ────────────────────────────────────────────────────────
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("voxel_grid", "VG",
                "VoxelGrid object from the VoxelGrid component.",
                GH_ParamAccess.item);
            pManager.AddNumberParameter("height", "H",
                "Height of each floor in model units.\n" +
                "Default: 10.0. Adjust to match your model units.",
                GH_ParamAccess.item, 10.0);
            pManager.AddIntegerParameter("mode", "M",
                "Analysis mode:\n" +
                "  0 = Floor Index (raw integer, positive up / negative down)\n" +
                "  1 = Walkup (5 at street, decreasing away, 0 at 7+ floors)\n" +
                "  2 = Commercial (highest at street, steps down going up)\n" +
                "  3 = Real Estate (progressive market value curve, normalized)",
                GH_ParamAccess.item, 0);
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
                "Per-voxel performance value based on selected mode.",
                GH_ParamAccess.list);
            pManager.AddPointParameter("centers", "C",
                "Voxel centres for preview.",
                GH_ParamAccess.list);
            pManager.AddColourParameter("gradient", "G",
                "Per-voxel gradient color from low (red) to high (blue).",
                GH_ParamAccess.list);
            pManager.AddTextParameter("info", "I",
                "Summary including floor distribution.",
                GH_ParamAccess.item);
        }

        // ── Solve ─────────────────────────────────────────────────────────────
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // ── Collect inputs ────────────────────────────────────────────────
            object voxelGridObj = null;
            double height = 10.0;
            int mode = 0;
            string label = "floor_level";

            if (!DA.GetData(0, ref voxelGridObj)) return;
            DA.GetData(1, ref height);
            DA.GetData(2, ref mode);
            DA.GetData(3, ref label);

            // ── Unwrap VoxelGrid ──────────────────────────────────────────────
            var voxelGrid = UnwrapVoxelGrid(voxelGridObj);
            if (voxelGrid == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Could not read VoxelGrid object.");
                return;
            }

            // ── Validate ──────────────────────────────────────────────────────
            string resolvedLabel = string.IsNullOrWhiteSpace(label)
                ? "floor_level" : label.Trim();

            if (height <= 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "height must be greater than 0.");
                return;
            }

            mode = Math.Max(0, Math.Min(3, mode));

            // ── Extract construction plane ────────────────────────────────────
            var planeOrigin = new Point3d(
                voxelGrid.PlaneToWorld.M03,
                voxelGrid.PlaneToWorld.M13,
                voxelGrid.PlaneToWorld.M23);

            var planeZAxis = new Vector3d(
                voxelGrid.PlaneToWorld.M02,
                voxelGrid.PlaneToWorld.M12,
                voxelGrid.PlaneToWorld.M22);

            planeZAxis.Unitize();

            // ── Compute floor level for each voxel ────────────────────────────
            var orderedKeys = voxelGrid.FilledKeys;
            var floorLevels = new List<int>();
            var floorCounts = new Dictionary<int, int>();

            foreach (var key in orderedKeys)
            {
                Point3d center = voxelGrid.KeyToCenter(key);
                Vector3d offset = center - planeOrigin;
                double h = Vector3d.Multiply(offset, planeZAxis);
                int floorLevel = ComputeFloorLevel(h, height);

                floorLevels.Add(floorLevel);

                if (!floorCounts.ContainsKey(floorLevel))
                    floorCounts[floorLevel] = 0;
                floorCounts[floorLevel]++;
            }

            // ── Find max floor level in grid ──────────────────────────────────
            int maxFloor = 1;
            foreach (var fl in floorLevels)
                if (fl > maxFloor) maxFloor = fl;

            // ── Build Mode 3 curve table ──────────────────────────────────────
            // Build progressive values floor by floor up to maxFloor
            // then handle penthouse as top 6% of floors
            Dictionary<int, double> mode3Table = null;
            if (mode == 3)
                mode3Table = BuildMode3Table(maxFloor);

            // ── Compute raw values ────────────────────────────────────────────
            var raw = new List<double>();
            for (int i = 0; i < floorLevels.Count; i++)
            {
                int fl = floorLevels[i];
                double value = 0.0;

                switch (mode)
                {
                    case 0:
                        value = ComputeMode0(fl);
                        break;
                    case 1:
                        value = ComputeMode1Walkup(fl);
                        break;
                    case 2:
                        value = ComputeMode2Commercial(fl);
                        break;
                    case 3:
                        value = (mode3Table != null && mode3Table.ContainsKey(fl))
                            ? mode3Table[fl]
                            : 0.0;
                        break;
                }

                raw.Add(value);
            }

            // ── Mode 3: normalize to [0,1] ────────────────────────────────────
            if (mode == 3 && raw.Count > 0)
            {
                double lo = raw[0];
                double hi = raw[0];

                foreach (var v in raw)
                {
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                }

                double span = hi - lo;
                if (span > 1e-12)
                {
                    for (int i = 0; i < raw.Count; i++)
                        raw[i] = (raw[i] - lo) / span;
                }
                else
                {
                    for (int i = 0; i < raw.Count; i++)
                        raw[i] = 0.0;
                }
            }

            // ── Build outputs ─────────────────────────────────────────────────
            var analysis = new SpatialAnalysis(resolvedLabel, raw);

            var centers = new List<Point3d>();
            foreach (var key in orderedKeys)
                centers.Add(voxelGrid.KeyToCenter(key));

            var gradient = ComputeGradient(raw);

            // ── Build preview ─────────────────────────────────────────────────
            BuildPreviewData(voxelGrid, raw);

            // ── Stats ─────────────────────────────────────────────────────────
            double rawMin = raw.Count > 0 ? raw[0] : 0;
            double rawMax = raw.Count > 0 ? raw[0] : 0;

            foreach (var v in raw)
            {
                if (v < rawMin) rawMin = v;
                if (v > rawMax) rawMax = v;
            }

            // ── Floor distribution ────────────────────────────────────────────
            var sortedFloors = new List<int>(floorCounts.Keys);
            sortedFloors.Sort();

            var floorLines = new System.Text.StringBuilder();
            foreach (var fl in sortedFloors)
            {
                double loH = fl > 0 ? (fl - 1) * height : fl * height;
                double hiH = fl > 0 ? fl * height : (fl + 1) * height;

                double displayVal = 0.0;
                switch (mode)
                {
                    case 0: displayVal = ComputeMode0(fl); break;
                    case 1: displayVal = ComputeMode1Walkup(fl); break;
                    case 2: displayVal = ComputeMode2Commercial(fl); break;
                    case 3:
                        displayVal = (mode3Table != null && mode3Table.ContainsKey(fl))
                            ? mode3Table[fl] : 0.0;
                        break;
                }

                floorLines.AppendLine(string.Format(
                    "  Floor {0,4} | {1,8:F1} to {2,8:F1} units | " +
                    "raw={3,10:F4} | voxels={4}",
                    fl, loH, hiH, displayVal, floorCounts[fl]));
            }

            string[] modeNames =
            {
                "Floor Index",
                "Walkup",
                "Commercial",
                "Real Estate (normalized)"
            };

            string info = string.Format(
                "SA_FloorLevel | label='{0}' | mode={1} ({2}) | " +
                "voxels={3} | height={4:F2} | floors={5} | " +
                "val_min={6:F4} val_max={7:F4}\n\n" +
                "Floor distribution:\n{8}",
                resolvedLabel,
                mode, modeNames[mode],
                raw.Count, height,
                floorCounts.Count,
                rawMin, rawMax,
                floorLines.ToString().TrimEnd());

            // ── Output ────────────────────────────────────────────────────────
            DA.SetData(0, analysis);
            DA.SetDataList(1, raw);
            DA.SetDataList(2, centers);
            DA.SetDataList(3, gradient);
            DA.SetData(4, info);
        }

        // ── Floor level from height above plane ───────────────────────────────
        private int ComputeFloorLevel(double heightAbovePlane, double floorHeight)
        {
            int rawFloor = (int)Math.Floor(heightAbovePlane / floorHeight);
            return rawFloor >= 0 ? rawFloor + 1 : rawFloor;
        }

        // ── Mode 0 — Floor Index ──────────────────────────────────────────────
        private double ComputeMode0(int floorLevel)
        {
            return (double)floorLevel;
        }

        // ── Mode 1 — Walkup ───────────────────────────────────────────────────
        private double ComputeMode1Walkup(int floorLevel)
        {
            int dist = Math.Abs(floorLevel - 1);
            if (dist >= 7) return 0.0;
            return Math.Max(1.0, 5.0 - (double)dist);
        }

        // ── Mode 2 — Commercial ───────────────────────────────────────────────
        private double ComputeMode2Commercial(int floorLevel)
        {
            if (floorLevel <= 0) return 0.0;
            return Math.Max(0.0, 5.0 - (double)(floorLevel - 1));
        }

        // ── Mode 3 — Real Estate curve table ──────────────────────────────────
        // Builds a dictionary of floor → raw value for floors 0..maxFloor
        // Penthouse = top 6% of floors
        private Dictionary<int, double> BuildMode3Table(int maxFloor)
        {
            var table = new Dictionary<int, double>();

            // Below ground = 0
            // Build progressive curve floor by floor from 1 to maxFloor
            double prev = 0.0;
            for (int f = 1; f <= maxFloor; f++)
            {
                double current;

                if (f == 1)
                {
                    // Ground floor base
                    current = 1.0;
                }
                else if (f <= 5)
                {
                    // Floors 2-5: +1% per floor
                    current = prev * 1.01;
                }
                else if (f == 6)
                {
                    // Floor 6: -1%
                    current = prev * 0.99;
                }
                else if (f == 7)
                {
                    // Floor 7: +3%
                    current = prev * 1.03;
                }
                else if (f <= 9)
                {
                    // Floors 8-9: +1.5% per floor
                    current = prev * 1.015;
                }
                else if (f == 10)
                {
                    // Floor 10: +5%
                    current = prev * 1.05;
                }
                else if (f <= 24)
                {
                    // Floors 11-24: +1.5% per floor
                    current = prev * 1.015;
                }
                else if (f <= 29)
                {
                    // Floors 25-29: +5% per floor
                    current = prev * 1.05;
                }
                else if (f <= 50)
                {
                    // Floors 30-50: +1.5% per floor
                    current = prev * 1.015;
                }
                else
                {
                    // Floors 51+: default +1.5% per floor
                    // Penthouse override applied below
                    current = prev * 1.015;
                }

                table[f] = current;
                prev = current;
            }

            // ── Penthouse: top 6% of floor levels ────────────────────────────
            // Penthouse starts at the floor that is 94% of the way up
            int penthouseStart = maxFloor - (int)Math.Floor(maxFloor * 0.06);
            penthouseStart = Math.Max(2, penthouseStart);

            // Calculate average of non-penthouse above-ground floors
            double sum = 0.0;
            int count = 0;

            for (int f = 1; f < penthouseStart; f++)
            {
                if (table.ContainsKey(f))
                {
                    sum += table[f];
                    count++;
                }
            }

            double avgValue = count > 0 ? sum / count : 1.0;

            // Penthouse minimum = avg * 1.35
            double penthouseMin = avgValue * 1.35;

            // First penthouse floor: at least penthouseMin
            // then +2% per floor after that
            double penthousePrev = 0.0;

            for (int f = penthouseStart; f <= maxFloor; f++)
            {
                double penthouseVal;

                if (f == penthouseStart)
                {
                    // First penthouse floor — ensure at least 35% above average
                    penthouseVal = Math.Max(
                        table.ContainsKey(f) ? table[f] : 0.0,
                        penthouseMin);
                }
                else
                {
                    // Subsequent penthouse floors: +2% per floor
                    penthouseVal = penthousePrev * 1.02;
                }

                table[f] = penthouseVal;
                penthousePrev = penthouseVal;
            }

            return table;
        }
    }
}
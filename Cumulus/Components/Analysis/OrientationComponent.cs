// -*- coding: utf-8 -*-
// Version 3.0.0
using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace Cumulus
{
    /// <summary>
    /// Which way each voxel faces, derived from which of its six face
    /// neighbours are missing.
    /// </summary>
    /// <remarks>
    /// RENAMED in 3.0.0 — was SA_Orientation / SA_Orient. The ComponentGuid is
    /// deliberately UNCHANGED so saved definitions still resolve to this class
    /// rather than appearing as unrecognised objects.
    ///
    /// The default label is unchanged at "orientation", so no ValueSet matrix
    /// needs re-confirming after this rename.
    ///
    /// NEW IN 3.0.0 — the mode input.
    ///
    /// Mode 0 reproduces the old behaviour exactly: a CATEGORY index naming the
    /// dominant open direction. Mode 1 is new and produces a CONTINUOUS
    /// alignment score against a target vector.
    ///
    /// The distinction matters because AnalysisStack normalises every channel
    /// it receives. Mode 0 values are labels, not magnitudes — min-max
    /// normalising 0..5 makes "+Y North" numerically 0.4 of "-Z Down", which is
    /// arithmetic performed on names. Nothing errors; the stack just produces a
    /// meaningless number. Mode 0 is therefore a DIAGNOSTIC output: read
    /// direction_indices and direction_vectors, do not feed the analysis
    /// channel into a stack.
    ///
    /// Mode 1 is stack-safe. It answers "how south-facing is this voxel" with a
    /// real ordering, so normalisation and multipliers behave sensibly.
    ///
    /// MODE 1 NORMAL DERIVATION — this is not a snap to one of six axes.
    /// The unit normals of every open face are SUMMED and the result unitized,
    /// so a voxel open on +X and +Y gets a diagonal normal rather than being
    /// forced onto whichever axis happened to win a tie. The alignment score is
    /// then cos(theta) between that aggregate normal and the target vector.
    /// </remarks>
    public class OrientationComponent : SAComponentBase
    {
        // -- Constructor -------------------------------------------------------
        public OrientationComponent()
            : base(
                "Orientation",
                "Orient",
                "Which way each voxel faces, from the pattern of its missing\n" +
                "face neighbours.\n\n" +
                "A voxel's open faces are the ones whose neighbour is empty.\n" +
                "Those faces are what the voxel presents to the outside.\n\n" +
                "Mode 0 — Category:\n" +
                "  The index of the dominant open direction.\n" +
                "    0 = +X (East)    1 = -X (West)\n" +
                "    2 = +Y (North)   3 = -Y (South)\n" +
                "    4 = +Z (Up)      5 = -Z (Down)\n" +
                "   -1 = interior, no open faces\n\n" +
                "  These are CATEGORY LABELS, not magnitudes. Do NOT wire the\n" +
                "  analysis output into AnalysisStack in this mode — it would\n" +
                "  normalise the indices and treat North as 0.4 of Down. Use\n" +
                "  direction_indices and direction_vectors instead.\n\n" +
                "Mode 1 — Alignment:\n" +
                "  How closely the voxel faces the target vector, as\n" +
                "  cos(angle) between them.\n" +
                "     1.0 = faces the target squarely\n" +
                "     0.0 = perpendicular to it\n" +
                "    -1.0 = faces directly away\n\n" +
                "  The facing normal is the SUM of the open face normals, so a\n" +
                "  corner voxel reads diagonally rather than snapping to one\n" +
                "  axis.\n\n" +
                "  This mode is continuous and ordered, so it normalises\n" +
                "  correctly and is safe to feed into AnalysisStack. Use it for\n" +
                "  questions like 'how south-facing is this voxel'.\n\n" +
                "target is read in WORLD coordinates, not the VoxelGrid\n" +
                "construction plane. A rotated grid needs a correspondingly\n" +
                "rotated target.\n\n" +
                "invert = False (default): values pass through.\n" +
                "invert = True: mode 1 only — reverses the alignment range, so\n" +
                "  facing away scores high. Ignored in mode 0, because\n" +
                "  reversing category indices would remap East to Down.\n\n" +
                "Version 3.0.0",
                "Cumulus",
                "2 | Analysis")
        { }

        // -- Ribbon placement --------------------------------------------------
        // Voxel-internal — reads only face-neighbour occupancy. The target
        // vector in mode 1 is a control input, not site context.
        public override GH_Exposure Exposure => GH_Exposure.secondary;

        // -- GUID — DO NOT CHANGE ----------------------------------------------
        // Kept from the SA_Orientation era so old .gh files keep working.
        public override Guid ComponentGuid =>
            new Guid("D4E5F6A7-B8C9-0123-DEFA-012345678911");

        // -- Icon --------------------------------------------------------------
        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "Cumulus.Resources.Orientation_24.png");
                return stream != null ? new Bitmap(stream) : null;
            }
        }

        // -- Face neighbour offsets, in index order ----------------------------
        private static readonly int[] DIR_DX = { 1, -1, 0, 0, 0, 0 };
        private static readonly int[] DIR_DY = { 0, 0, 1, -1, 0, 0 };
        private static readonly int[] DIR_DZ = { 0, 0, 0, 0, 1, -1 };

        private static readonly string[] DIR_NAMES =
        {
            "+X (East)", "-X (West)",
            "+Y (North)", "-Y (South)",
            "+Z (Up)", "-Z (Down)"
        };

        private static readonly Vector3d[] DIR_VECTORS =
        {
            new Vector3d( 1,  0,  0),
            new Vector3d(-1,  0,  0),
            new Vector3d( 0,  1,  0),
            new Vector3d( 0, -1,  0),
            new Vector3d( 0,  0,  1),
            new Vector3d( 0,  0, -1),
        };

        // -- Mode names --------------------------------------------------------
        private static readonly string[] MODE_NAMES =
        {
            "Category (direction index)",
            "Alignment (cos angle to target)"
        };

        // -- Parameters --------------------------------------------------------
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("voxel_grid", "VG",
                "VoxelGrid object from the VoxelGrid component.",
                GH_ParamAccess.item);
            pManager.AddIntegerParameter("mode", "M",
                "Output mode:\n" +
                "  0 = Category  (direction index 0-5, -1 interior)\n" +
                "  1 = Alignment (cos angle to target, -1 to +1)\n" +
                "Mode 0 is diagnostic only — do not feed it to AnalysisStack.",
                GH_ParamAccess.item, 0);
            pManager.AddVectorParameter("target", "T",
                "Mode 1 only. The direction to measure alignment against, in\n" +
                "WORLD coordinates.\n" +
                "Default: (0, -1, 0) — south in a standard Rhino setup.\n" +
                "Unitized internally, so magnitude is ignored.",
                GH_ParamAccess.item, new Vector3d(0.0, -1.0, 0.0));
            pManager.AddBooleanParameter("invert", "I",
                "Mode 1 only. If True, reverse the alignment range so facing\n" +
                "AWAY from the target scores high.\n" +
                "Ignored in mode 0 — reversing category indices would remap\n" +
                "East to Down. A warning is raised if set in mode 0.\n" +
                "Default: false.",
                GH_ParamAccess.item, false);
            pManager.AddTextParameter("label", "L",
                "Channel name used by AnalysisStack. Default: 'orientation'.",
                GH_ParamAccess.item, "orientation");
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("analysis", "A",
                "SpatialAnalysis object.\n" +
                "Mode 1: wire into AnalysisStack.\n" +
                "Mode 0: diagnostic only — see the mode description.",
                GH_ParamAccess.item);
            pManager.AddNumberParameter("values", "V",
                "Mode 0: dominant direction index, -1 for interior.\n" +
                "Mode 1: cos(angle) to target, inverted if Invert is true.",
                GH_ParamAccess.list);
            pManager.AddPointParameter("centers", "C",
                "Voxel centres for preview.",
                GH_ParamAccess.list);
            pManager.AddColourParameter("gradient", "G",
                "Per-voxel gradient color from low (red) to high (blue).\n" +
                "In mode 0 this is a category map, not a performance ramp.",
                GH_ParamAccess.list);
            pManager.AddIntegerParameter("direction_indices", "DI",
                "Dominant open direction index per voxel, 0-5, or -1 for\n" +
                "interior. Never inverted, never remapped — identical in both\n" +
                "modes.",
                GH_ParamAccess.list);
            pManager.AddVectorParameter("direction_vectors", "D",
                "Per-voxel facing direction as a unit vector.\n" +
                "Mode 0: the dominant axis.\n" +
                "Mode 1: the aggregate open-face normal, which may be\n" +
                "  diagonal.\n" +
                "Zero vector for interior voxels. Never inverted.",
                GH_ParamAccess.list);
            pManager.AddIntegerParameter("open_face_counts", "OF",
                "How many of the six faces are open per voxel, 0-6.\n" +
                "0 = fully interior, 6 = fully isolated.",
                GH_ParamAccess.list);
            pManager.AddTextParameter("info", "I",
                "Summary including the per-direction voxel counts.",
                GH_ParamAccess.item);
        }

        // -- Solve -------------------------------------------------------------
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            object voxelGridObj = null;
            int mode = 0;
            var target = new Vector3d(0.0, -1.0, 0.0);
            bool invert = false;
            string label = "orientation";

            if (!DA.GetData(0, ref voxelGridObj)) return;
            DA.GetData(1, ref mode);
            DA.GetData(2, ref target);
            DA.GetData(3, ref invert);
            DA.GetData(4, ref label);

            var voxelGrid = UnwrapVoxelGrid(voxelGridObj);
            if (voxelGrid == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Could not read VoxelGrid object.");
                return;
            }

            mode = Math.Max(0, Math.Min(1, mode));

            string resolvedLabel = string.IsNullOrWhiteSpace(label)
                ? "orientation" : label.Trim();

            // -- Target vector, mode 1 only ------------------------------------
            if (mode == 1)
            {
                if (target.Length < 1e-12)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        "target is a zero-length vector — cannot measure\n" +
                        "alignment against it. Supply a direction.");
                    return;
                }
                target.Unitize();
            }

            // Invert is meaningless on category indices. Say so rather than
            // silently ignoring the input the user deliberately set.
            if (mode == 0 && invert)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "invert is ignored in mode 0. Reversing category indices\n" +
                    "would remap East to Down. Switch to mode 1 to invert an\n" +
                    "alignment score.");
            }

            // -- Per-voxel orientation -----------------------------------------
            var orderedKeys = voxelGrid.FilledKeys;
            var filled = voxelGrid.FilledKeysSet;

            var raw = new List<double>(orderedKeys.Count);
            var dirIndices = new List<int>(orderedKeys.Count);
            var dirVectors = new List<Vector3d>(orderedKeys.Count);
            var openFaceCounts = new List<int>(orderedKeys.Count);
            var dirCounts = new int[6];

            int nInterior = 0;
            int nIsolated = 0;

            foreach (var key in orderedKeys)
            {
                int ix = key.Item1;
                int iy = key.Item2;
                int iz = key.Item3;

                // -- Which faces are open --------------------------------------
                bool[] isOpen = new bool[6];
                int totalOpen = 0;

                for (int d = 0; d < 6; d++)
                {
                    var nb = (ix + DIR_DX[d], iy + DIR_DY[d], iz + DIR_DZ[d]);
                    if (!filled.Contains(nb))
                    {
                        isOpen[d] = true;
                        totalOpen++;
                    }
                }

                openFaceCounts.Add(totalOpen);

                // -- Fully interior — no facing direction at all ---------------
                if (totalOpen == 0)
                {
                    nInterior++;
                    dirIndices.Add(-1);
                    dirVectors.Add(Vector3d.Zero);

                    // Mode 0: -1 is the interior sentinel.
                    // Mode 1: an interior voxel faces nothing, so it is
                    // minimally aligned with any target.
                    raw.Add(mode == 0 ? -1.0 : -1.0);
                    continue;
                }

                // -- Dominant open direction -----------------------------------
                // With one open face per direction the counts are 0 or 1, so
                // ties resolve to the lowest index. That is the 2.0.0
                // behaviour and is preserved deliberately.
                int dominantDir = -1;
                for (int d = 0; d < 6; d++)
                {
                    if (isOpen[d]) { dominantDir = d; break; }
                }

                dirIndices.Add(dominantDir);
                dirCounts[dominantDir]++;

                // -- Aggregate open-face normal --------------------------------
                var aggregate = Vector3d.Zero;
                for (int d = 0; d < 6; d++)
                    if (isOpen[d]) aggregate += DIR_VECTORS[d];

                bool normalCancels = aggregate.Length < 1e-12;
                if (normalCancels) nIsolated++;

                if (mode == 0)
                {
                    raw.Add((double)dominantDir);
                    dirVectors.Add(DIR_VECTORS[dominantDir]);
                }
                else
                {
                    if (normalCancels)
                    {
                        // Open on opposing faces that cancel exactly — a fully
                        // isolated voxel, or a slab open top and bottom. There
                        // is no net facing direction, so alignment is neutral
                        // rather than opposed.
                        raw.Add(0.0);
                        dirVectors.Add(Vector3d.Zero);
                    }
                    else
                    {
                        aggregate.Unitize();
                        raw.Add(aggregate * target);   // dot product = cos theta
                        dirVectors.Add(aggregate);
                    }
                }
            }

            // -- Invert, mode 1 only -------------------------------------------
            bool effectiveInvert = invert && mode == 1;
            var outputValues = effectiveInvert ? InvertValues(raw) : raw;

            var analysis = new SpatialAnalysis(resolvedLabel, outputValues);

            var centers = new List<Point3d>(orderedKeys.Count);
            foreach (var key in orderedKeys)
                centers.Add(voxelGrid.KeyToCenter(key));

            var gradient = ComputeGradient(outputValues);
            BuildPreviewData(voxelGrid, outputValues);

            // -- Stats ---------------------------------------------------------
            double outMin = outputValues.Count > 0 ? outputValues[0] : 0.0;
            double outMax = outMin;
            double outSum = 0.0;
            foreach (var value in outputValues)
            {
                if (value < outMin) outMin = value;
                if (value > outMax) outMax = value;
                outSum += value;
            }
            double outMean = outputValues.Count > 0
                ? outSum / outputValues.Count : 0.0;

            // -- Info ----------------------------------------------------------
            var sb = new System.Text.StringBuilder();

            sb.AppendLine(string.Format(
                "Orientation | label='{0}' | mode={1} ({2}) | voxels={3}",
                resolvedLabel, mode, MODE_NAMES[mode], raw.Count));

            if (mode == 1)
            {
                sb.AppendLine(string.Format(
                    "target=({0:F3}, {1:F3}, {2:F3}) world | invert={3}",
                    target.X, target.Y, target.Z, effectiveInvert));
                sb.AppendLine(string.Format(
                    "output=[{0:F4} to {1:F4}] | mean={2:F4} (cos angle)",
                    outMin, outMax, outMean));
            }
            else
            {
                sb.AppendLine(string.Format(
                    "output=[{0:F0} to {1:F0}] (category indices — NOT a scale)",
                    outMin, outMax));
                sb.AppendLine(
                    "Do not wire 'analysis' into AnalysisStack in this mode.");
            }

            sb.AppendLine(string.Format(
                "interior={0} (no open faces) | normal_cancels={1}",
                nInterior, nIsolated));

            sb.AppendLine("");
            sb.AppendLine("Dominant direction counts:");
            for (int d = 0; d < 6; d++)
            {
                double pct = raw.Count > 0
                    ? dirCounts[d] * 100.0 / raw.Count : 0.0;
                sb.AppendLine(string.Format(
                    "  {0,-12} {1,7}  ({2,5:F1}%)",
                    DIR_NAMES[d], dirCounts[d], pct));
            }

            DA.SetData(0, analysis);
            DA.SetDataList(1, outputValues);
            DA.SetDataList(2, centers);
            DA.SetDataList(3, gradient);
            DA.SetDataList(4, dirIndices);
            DA.SetDataList(5, dirVectors);
            DA.SetDataList(6, openFaceCounts);
            DA.SetData(7, sb.ToString().TrimEnd());
        }
    }
}

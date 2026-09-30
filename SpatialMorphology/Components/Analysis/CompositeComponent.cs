// -*- coding: utf-8 -*-
// Version 2.0.0
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace SpatialMorphology
{
    /// <summary>
    /// Thin Grasshopper adapter over <see cref="CompositeAnalysisEngine"/>.
    /// Fuses several SpatialAnalysis channels into one derived channel.
    /// No combination logic lives here.
    /// </summary>
    /// <remarks>
    /// RENAMED in 2.0.0 — was SA_Composite / SAComp. The ComponentGuid is
    /// deliberately UNCHANGED so saved definitions still resolve to this class
    /// rather than appearing as unrecognised objects.
    ///
    /// The default label is unchanged at "composite", so no ValueSet matrix
    /// needs re-confirming after this rename.
    ///
    /// This component sits in the contextual half of the Analysis panel because
    /// it consumes channels produced by other components rather than reading the
    /// voxel grid directly. Its voxel_grid input exists only for preview.
    /// </remarks>
    public class CompositeComponent : SAComponentBase
    {
        // ── Constructor ───────────────────────────────────────────────────────
        public CompositeComponent()
            : base(
                "Composite",
                "Comp",
                "Combines several SpatialAnalysis channels into ONE derived\n" +
                "channel, so an abstract quality can be built from concrete\n" +
                "measurements.\n\n" +
                "Example — privacy:\n" +
                "  analysis    = [Visibility, Proximity]\n" +
                "  multipliers = [-1.0, -1.0]\n" +
                "  label       = 'privacy'\n\n" +
                "Every input channel is normalised to 0..1 INDEPENDENTLY before\n" +
                "combining, so channels on different scales (metres vs ray\n" +
                "counts) contribute according to their multiplier rather than\n" +
                "their units.\n\n" +
                "Multipliers use the same convention as ValueSet:\n" +
                "  +1.0 = prefer HIGH values\n" +
                "  -1.0 = prefer LOW values\n" +
                "   0.0 = exclude this channel\n\n" +
                "mode:\n" +
                "  0 = Weighted sum — weighted mean of all channels.\n" +
                "      Multiplier MAGNITUDE scales each contribution.\n" +
                "  1 = Minimum — the worst channel wins. Use when the quality\n" +
                "      requires ALL constituents to be satisfied: a voxel\n" +
                "      screened on five sides but open on the sixth is NOT\n" +
                "      private.\n" +
                "  2 = Maximum — the best channel wins. Use when ANY\n" +
                "      constituent is sufficient.\n" +
                "  In modes 1 and 2 the multiplier magnitude is IGNORED; only\n" +
                "  the sign matters (0.0 still excludes).\n\n" +
                "invert = False (default): composite values pass through.\n" +
                "invert = True: the composite range is reversed, so low becomes\n" +
                "  high. Applied AFTER combining, so it flips the finished\n" +
                "  quality rather than any single channel.\n\n" +
                "Connect voxel_grid to enable the coloured voxel preview and the\n" +
                "centers / gradient outputs. It is optional — without it the\n" +
                "component still produces a valid composite channel.\n\n" +
                "Output is a normal SpatialAnalysis — wire it into ValueSet and\n" +
                "AnalysisStack exactly like any analysis component.\n\n" +
                "Also works with a single input as a re-labeller, which is\n" +
                "useful because AnalysisStack rejects duplicate channel labels:\n" +
                "give the second copy of a channel a distinct label here.\n\n" +
                "Version 2.0.0",
                "Spatial Morphology",
                "2 | Analysis")
        { }

        // ── Ribbon placement ──────────────────────────────────────────────────
        // Contextual — consumes channels from other components.
        public override GH_Exposure Exposure => GH_Exposure.primary;

        // ── GUID — DO NOT CHANGE ──────────────────────────────────────────────
        // Kept from the SA_Composite era so old .gh files keep working.
        public override Guid ComponentGuid =>
            new Guid("E5F6A7B8-C9D0-1234-EFAB-012345678920");

        // ── Icon ──────────────────────────────────────────────────────────────
        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "SpatialMorphology.Resources.Composite_24.png");
                return stream != null ? new Bitmap(stream) : null;
            }
        }

        // ── Parameters ────────────────────────────────────────────────────────
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("voxel_grid", "VG",
                "Optional. VoxelGrid object from the VoxelGrid component.\n" +
                "Required only for the voxel preview and the centers /\n" +
                "gradient outputs. The composite channel is computed either\n" +
                "way.",
                GH_ParamAccess.item);
            pManager.AddGenericParameter("analysis", "A",
                "Two or more SpatialAnalysis objects to combine.\n" +
                "All must come from the same voxel grid.",
                GH_ParamAccess.list);
            pManager.AddNumberParameter("multipliers", "M",
                "Signed multiplier per channel, parallel to 'analysis'.\n" +
                "  +1.0 = prefer HIGH   -1.0 = prefer LOW   0.0 = exclude\n" +
                "Missing entries default to +1.0.",
                GH_ParamAccess.list);
            pManager.AddBooleanParameter("invert", "I",
                "If True, reverse the composite value range so low becomes\n" +
                "high and high becomes low. Applied after combining, so it\n" +
                "inverts the finished quality rather than any individual\n" +
                "channel. The gradient and voxel preview are reversed to match.\n" +
                "Default: false.",
                GH_ParamAccess.item, false);
            pManager.AddTextParameter("label", "L",
                "Label for the resulting channel, e.g. 'privacy'.\n" +
                "Must be unique across all channels reaching one AnalysisStack.",
                GH_ParamAccess.item, "composite");
            pManager.AddIntegerParameter("mode", "MD",
                "0 = Weighted sum\n" +
                "1 = Minimum (worst channel wins)\n" +
                "2 = Maximum (best channel wins)",
                GH_ParamAccess.item, 0);

            pManager[0].Optional = true;
            pManager[2].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("analysis", "A",
                "Composite SpatialAnalysis object, values in 0..1.\n" +
                "Inverted if invert is true.\n" +
                "Wire into ValueSet and AnalysisStack like any analysis.",
                GH_ParamAccess.item);
            pManager.AddNumberParameter("values", "V",
                "Composite values per voxel, parallel to\n" +
                "voxel_grid.FilledKeys. Inverted if invert is true.",
                GH_ParamAccess.list);
            pManager.AddPointParameter("centers", "C",
                "Voxel centres for preview.\n" +
                "Empty unless voxel_grid is connected.",
                GH_ParamAccess.list);
            pManager.AddColourParameter("gradient", "G",
                "Per-voxel gradient color from low (red) to high (blue),\n" +
                "based on the output values.\n" +
                "Empty unless voxel_grid is connected.",
                GH_ParamAccess.list);
            pManager.AddTextParameter("info", "I",
                "Summary of inputs, multipliers, mode, and output range.",
                GH_ParamAccess.item);
        }

        // ── Solve ─────────────────────────────────────────────────────────────
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // ── Collect inputs ────────────────────────────────────────────────
            object voxelGridObj = null;
            var analysisObjects = new List<object>();
            var multipliers = new List<double>();
            bool invert = false;
            string label = "composite";
            int mode = 0;

            DA.GetData(0, ref voxelGridObj);
            if (!DA.GetDataList(1, analysisObjects)) return;
            DA.GetDataList(2, multipliers);
            DA.GetData(3, ref invert);
            DA.GetData(4, ref label);
            DA.GetData(5, ref mode);

            mode = Math.Max(0, Math.Min(2, mode));

            // ── Unwrap SpatialAnalysis objects ────────────────────────────────
            var saList = new List<SpatialAnalysis>();
            foreach (var obj in analysisObjects)
            {
                var inner = obj is GH_ObjectWrapper w ? w.Value : obj;

                if (inner is SpatialAnalysis sa)
                {
                    saList.Add(sa);
                    continue;
                }

                // Tolerate duck-typed objects from GHPython / C# script
                // components, matching how AnalysisStack unwraps its inputs.
                if (inner != null)
                {
                    try
                    {
                        dynamic d = inner;
                        string lbl = d.label?.ToString();
                        var vals = d.values;
                        if (!string.IsNullOrWhiteSpace(lbl) && vals != null)
                        {
                            var vlist = new List<double>();
                            foreach (var v in vals)
                                vlist.Add(Convert.ToDouble(v));
                            saList.Add(new SpatialAnalysis(lbl, vlist));
                        }
                    }
                    catch { }
                }
            }

            if (saList.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "No SpatialAnalysis objects found in 'analysis'.");
                return;
            }

            if (saList.Count == 1)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "Only one channel supplied — acting as a re-labeller. " +
                    "Connect two or more analyses to combine them.");
            }

            if (multipliers.Count > 0 && multipliers.Count != saList.Count)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    string.Format(
                        "{0} multipliers supplied for {1} channels. " +
                        "Missing entries default to +1.0; extras are ignored.",
                        multipliers.Count, saList.Count));
            }

            // ── Call Core ─────────────────────────────────────────────────────
            SpatialAnalysis composite;
            try
            {
                composite = CompositeAnalysisEngine.Run(
                    label,
                    saList,
                    multipliers,
                    (CompositeMode)mode);
            }
            catch (CompositeInputException ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                return;
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Composite failed: " + ex.Message);
                return;
            }

            // ── Invert AFTER combining ────────────────────────────────────────
            // Flips the finished quality rather than any single channel. A new
            // SpatialAnalysis is built so downstream ValueSet / AnalysisStack
            // receive the inverted values, not the engine's originals.
            var outputValues = invert
                ? InvertValues(composite.Values)
                : new List<double>(composite.Values);

            var outputAnalysis = invert
                ? new SpatialAnalysis(composite.Label, outputValues)
                : composite;

            // ── Optional preview ──────────────────────────────────────────────
            var centers = new List<Point3d>();
            var gradient = new List<Color>();
            string previewNote;

            var voxelGrid = voxelGridObj != null
                ? UnwrapVoxelGrid(voxelGridObj) : null;

            if (voxelGrid == null)
            {
                previewNote = voxelGridObj == null
                    ? "preview: off (connect voxel_grid)"
                    : "preview: off (voxel_grid unreadable)";

                if (voxelGridObj != null)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        "Could not read VoxelGrid object — preview disabled.");

                _previewPoints = new List<Point3d>();
                _previewColors = new List<Color>();
            }
            else if (voxelGrid.FilledKeys.Count != outputValues.Count)
            {
                // Channels from a different grid than the one connected here.
                previewNote = string.Format(
                    "preview: off (grid has {0} voxels, channels have {1})",
                    voxelGrid.FilledKeys.Count, outputValues.Count);

                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    string.Format(
                        "voxel_grid has {0} filled voxels but the combined " +
                        "channels have {1} values. Preview disabled — the " +
                        "channels likely come from a different grid.",
                        voxelGrid.FilledKeys.Count, outputValues.Count));

                _previewPoints = new List<Point3d>();
                _previewColors = new List<Color>();
            }
            else
            {
                foreach (var key in voxelGrid.FilledKeys)
                    centers.Add(voxelGrid.KeyToCenter(key));

                gradient = ComputeGradient(outputValues);
                BuildPreviewData(voxelGrid, outputValues);
                previewNote = "preview: on";
            }

            // ── Info ──────────────────────────────────────────────────────────
            string[] modeNames = { "Weighted sum", "Minimum", "Maximum" };

            var lines = new System.Text.StringBuilder();
            lines.AppendLine(string.Format(
                "Composite | label='{0}' | mode={1} | voxels={2} | invert={3}",
                outputAnalysis.Label, modeNames[mode],
                outputValues.Count, invert));
            lines.AppendLine(previewNote);
            lines.AppendLine("");
            lines.AppendLine("Inputs:");

            for (int i = 0; i < saList.Count; i++)
            {
                double m = i < multipliers.Count ? multipliers[i] : 1.0;

                string direction;
                if (m == 0.0) direction = "excluded";
                else if (m > 0.0) direction = "prefer HIGH";
                else direction = "prefer LOW";

                // Magnitude is meaningless in min/max mode — say so rather than
                // printing a number the user might assume is being applied.
                string shown = (mode == 0)
                    ? string.Format("{0:+0.00;-0.00}", m)
                    : (m == 0.0 ? " 0.00" : string.Format("{0:+1.00;-1.00}", m));

                lines.AppendLine(string.Format(
                    "  '{0}'  m={1}  ({2})  raw=[{3:F3} -> {4:F3}]",
                    saList[i].Label,
                    shown,
                    direction,
                    saList[i].Values.Count > 0 ? saList[i].Values.Min() : 0.0,
                    saList[i].Values.Count > 0 ? saList[i].Values.Max() : 0.0));
            }

            if (mode != 0)
                lines.AppendLine(
                    "  (magnitude ignored in this mode — sign only)");

            lines.AppendLine("");
            lines.AppendLine(string.Format(
                "Output '{0}' range: [{1:F3} -> {2:F3}]{3}",
                outputAnalysis.Label,
                outputValues.Count > 0 ? outputValues.Min() : 0.0,
                outputValues.Count > 0 ? outputValues.Max() : 0.0,
                invert ? "  (inverted)" : ""));
            lines.AppendLine("");
            lines.AppendLine(
                "Note: do NOT also pass the constituent channels into the same\n" +
                "AnalysisStack — they would be counted twice.");

            // ── Output ────────────────────────────────────────────────────────
            DA.SetData(0, outputAnalysis);
            DA.SetDataList(1, outputValues);
            DA.SetDataList(2, centers);
            DA.SetDataList(3, gradient);
            DA.SetData(4, lines.ToString().TrimEnd());
        }
    }
}

// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Cumulus.Components.Data;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Display;
using Rhino.Geometry;

namespace Cumulus
{
    /// <summary>
    /// Replaces program-assigned AnalysisStack voxels with a deterministic,
    /// largest-first set of axis-aligned Rhino boxes for preview and export.
    /// </summary>
    public sealed class MergeProgramVoxelsComponent : GH_Component
    {
        private readonly List<PreviewBox> _previewBoxes = new List<PreviewBox>();

        public MergeProgramVoxelsComponent()
            : base(
                "Merge Program Voxels",
                "MergeVox",
                "Combines assigned AnalysisStack voxels into the largest valid " +
                "axis-aligned boxes. Each box contains one program only; core " +
                "voxels are merged as a separate group and unassigned voxels " +
                "remain excluded.",
                "Cumulus",
                "4 | Post Process")
        {
        }

        public override Guid ComponentGuid =>
            new Guid("E4421492-BDA1-4967-9241-6ADA1FD15C5F");

        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "Cumulus.Resources.Merge_24.png");
                return stream != null ? new Bitmap(stream) : null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "analysis_stack",
                "AS",
                "AnalysisStack object from the AnalysisStack component.",
                GH_ParamAccess.item);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddBoxParameter(
                "boxes",
                "B",
                "Merged, world-space Rhino boxes. Their axes follow the VoxelGrid construction plane.",
                GH_ParamAccess.list);
            pManager.AddIntegerParameter(
                "program_indices",
                "PI",
                "Program assignment parallel to boxes. -2 denotes the reserved core.",
                GH_ParamAccess.list);
            pManager.AddTextParameter(
                "program_names",
                "PN",
                "Program label parallel to boxes. Core boxes are labelled 'Core'.",
                GH_ParamAccess.list);
            pManager.AddIntegerParameter(
                "voxel_counts",
                "VC",
                "Number of original voxels represented by each box.",
                GH_ParamAccess.list);
            pManager.AddColourParameter(
                "colors",
                "C",
                "Display color parallel to boxes.",
                GH_ParamAccess.list);
            pManager.AddTextParameter(
                "report",
                "R",
                "Summary of the voxel-to-box reduction.",
                GH_ParamAccess.item);
        }

        protected override void BeforeSolveInstance()
        {
            _previewBoxes.Clear();
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            object stackObject = null;
            if (!DA.GetData(0, ref stackObject)) return;

            var stack = UnwrapStack(stackObject);
            if (stack == null)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Could not read an AnalysisStackData object. Connect the 'analysis_stack' output from AnalysisStack.");
                return;
            }

            if (stack.VoxelGrid == null ||
                stack.ProgramIndices == null ||
                stack.VoxelGrid.FilledKeys == null)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "AnalysisStackData is incomplete.");
                return;
            }

            if (stack.VoxelGrid.FilledKeys.Count != stack.ProgramIndices.Count)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "AnalysisStackData has a different number of voxel keys and program assignments.");
                return;
            }

            var keys = stack.VoxelGrid.FilledKeys
                .Select(k => new VoxelBoxKey(k.Item1, k.Item2, k.Item3))
                .ToList();

            IReadOnlyList<VoxelBoxRegion> regions;
            try
            {
                regions = VoxelBoxMergeEngine.Merge(keys, stack.ProgramIndices);
            }
            catch (ArgumentException ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                return;
            }

            var boxes = new List<Box>(regions.Count);
            var programIndices = new List<int>(regions.Count);
            var programNames = new List<string>(regions.Count);
            var voxelCounts = new List<int>(regions.Count);
            var colors = new List<Color>(regions.Count);

            foreach (VoxelBoxRegion region in regions)
            {
                Box box = RegionToBox(stack.VoxelGrid, region);
                Color color = ColorFor(stack, region.ProgramIndex);
                string name = NameFor(stack, region.ProgramIndex);

                boxes.Add(box);
                programIndices.Add(region.ProgramIndex);
                programNames.Add(name);
                voxelCounts.Add(region.VoxelCount);
                colors.Add(color);
                _previewBoxes.Add(new PreviewBox(box, color));
            }

            int occupied = stack.ProgramIndices.Count(i => i != -1);
            double reduction = occupied == 0
                ? 0.0
                : 100.0 * (occupied - boxes.Count) / occupied;

            string report = string.Format(
                "Merge Program Voxels | occupied voxels={0} | merged boxes={1} | reduction={2:F1}%\n" +
                "Unassigned voxels are excluded. Each merged box contains only one program or reserved core voxels.",
                occupied,
                boxes.Count,
                reduction);

            DA.SetDataList(0, boxes);
            DA.SetDataList(1, programIndices);
            DA.SetDataList(2, programNames);
            DA.SetDataList(3, voxelCounts);
            DA.SetDataList(4, colors);
            DA.SetData(5, report);
        }

        public override void DrawViewportMeshes(IGH_PreviewArgs args)
        {
            foreach (PreviewBox preview in _previewBoxes)
            {
                Brep brep = preview.Box.ToBrep();
                try
                {
                    args.Display.DrawBrepShaded(
                        brep,
                        new DisplayMaterial(preview.Color));
                }
                finally
                {
                    brep.Dispose();
                }
            }
        }

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            foreach (PreviewBox preview in _previewBoxes)
                args.Display.DrawBox(preview.Box, Color.Black);
        }

        private static AnalysisStackData UnwrapStack(object value)
        {
            var wrapper = value as GH_ObjectWrapper;
            object inner = wrapper != null ? wrapper.Value : value;
            return inner as AnalysisStackData;
        }

        private static string NameFor(AnalysisStackData stack, int programIndex)
        {
            if (programIndex == -2) return "Core";
            if (programIndex >= 0 && programIndex < stack.Programs.Count)
                return stack.Programs[programIndex].Name;
            return string.Format("Program {0}", programIndex);
        }

        private static Color ColorFor(AnalysisStackData stack, int programIndex)
        {
            if (programIndex == -2) return Color.FromArgb(180, 80, 80, 80);
            if (programIndex >= 0 && programIndex < stack.Programs.Count)
            {
                Color color = stack.Programs[programIndex].Color;
                return Color.FromArgb(220, color.R, color.G, color.B);
            }

            return Color.Magenta;
        }

        private static Box RegionToBox(VoxelGrid grid, VoxelBoxRegion region)
        {
            Box first = grid.KeyToBox((
                region.Minimum.X,
                region.Minimum.Y,
                region.Minimum.Z));

            Plane plane = first.Plane;
            double size = grid.VoxelSize;

            return new Box(
                plane,
                new Interval(0.0, region.Width * size),
                new Interval(0.0, region.Depth * size),
                new Interval(0.0, region.Height * size));
        }

        private sealed class PreviewBox
        {
            public Box Box { get; }
            public Color Color { get; }

            public PreviewBox(Box box, Color color)
            {
                Box = box;
                Color = color;
            }
        }
    }
}

// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;

namespace SpatialMorphology
{
    public class ProgramDefinitionComponent : GH_Component
    {
        // ── Constructor ───────────────────────────────────────────────────────
        public ProgramDefinitionComponent()
            : base(
                "ProgramDefinition",
                "ProgDef",
                "Defines a programmatic space in the voxel model.",
                "Spatial Morphology",
                "Setup")
        { }

        // ── GUID ──────────────────────────────────────────────────────────────
        public override Guid ComponentGuid =>
            new Guid("C1D2E3F4-A5B6-7890-CDEF-012345678902");

        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "SpatialMorphology.Resources.ProgramDefinition_24.png");
                return stream != null ? new Bitmap(stream) : null;
            }
        }

        // ── Parameters ────────────────────────────────────────────────────────
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("name", "N",
                "Human-readable program label, e.g. 'Office'.",
                GH_ParamAccess.item);
            pManager.AddColourParameter("color", "C",
                "Display colour for this program.",
                GH_ParamAccess.item, Color.White);
            pManager.AddIntegerParameter("voxel_count", "V",
                "Target number of voxels. -1 = unlimited.",
                GH_ParamAccess.item, -1);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("program", "P",
                "ProgramDefinition object. Wire multiple into a merged list into AnalysisStack.",
                GH_ParamAccess.item);
            pManager.AddTextParameter("info", "I",
                "Human-readable summary.",
                GH_ParamAccess.item);
        }

        // ── Solve ─────────────────────────────────────────────────────────────
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // ── Collect inputs ────────────────────────────────────────────────
            string name = string.Empty;
            Color color = Color.White;
            int voxelCount = -1;

            if (!DA.GetData(0, ref name)) return;
            DA.GetData(1, ref color);
            DA.GetData(2, ref voxelCount);

            // ── Validate ──────────────────────────────────────────────────────
            if (string.IsNullOrWhiteSpace(name))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "'name' must be a non-empty string.");
                return;
            }

            if (voxelCount == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "voxel_count=0 means this program can never be assigned any voxels. " +
                    "Use -1 for unlimited or a positive integer.");
                return;
            }

            // ── Build ProgramDefinition ───────────────────────────────────────
            var program = new ProgramDefinition(
                name.Trim(),
                color,
                voxelCount);

            // ── Info string ───────────────────────────────────────────────────
            string limit = voxelCount >= 0
                ? voxelCount.ToString()
                : "unlimited";

            string info = string.Format(
                "ProgramDefinition\n" +
                "  name        : {0}\n" +
                "  color       : R={1} G={2} B={3}\n" +
                "  voxel_count : {4}",
                program.Name,
                program.Color.R,
                program.Color.G,
                program.Color.B,
                limit);

            // ── Output ────────────────────────────────────────────────────────
            DA.SetData(0, program);
            DA.SetData(1, info);
        }
    }

    // ── ProgramDefinition data class ──────────────────────────────────────────
    public class ProgramDefinition
    {
        public string Name { get; private set; }
        public Color Color { get; private set; }
        public int VoxelCount { get; private set; }

        public ProgramDefinition(string name, Color color, int voxelCount = -1)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("name must be a non-empty string.");
            if (voxelCount == 0)
                throw new ArgumentException(
                    "voxel_count=0 means this program can never be assigned voxels. " +
                    "Use -1 for unlimited or a positive integer.");

            Name = name.Trim();
            Color = Color.FromArgb(255, color.R, color.G, color.B);
            VoxelCount = voxelCount;
        }

        public override string ToString()
        {
            string limit = VoxelCount >= 0 ? VoxelCount.ToString() : "unlimited";
            return string.Format(
                "ProgramDefinition(name='{0}', color=({1},{2},{3}), voxel_count={4})",
                Name, Color.R, Color.G, Color.B, limit);
        }
    }
}
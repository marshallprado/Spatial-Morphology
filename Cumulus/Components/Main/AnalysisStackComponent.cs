// -*- coding: utf-8 -*-
// Version 1.2.0
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace Cumulus.Components.Data
{
    /// <summary>
    /// Thin Grasshopper adapter over <see cref="VoxelScoringEngine"/>.
    /// Reads inputs, calls Core, builds DataTrees. No scoring logic lives here.
    /// </summary>
    public class AnalysisStackComponent : GH_Component
    {
        // Preview data is independent of the shader output. The output retains
        // score alpha; viewport colors are intentionally rendered opaque.
        private readonly List<Point3d> _previewPoints = new List<Point3d>();
        private readonly List<Box> _previewBoxes = new List<Box>();
        private readonly List<Color> _previewColors = new List<Color>();
        private bool _previewAsBoxes;
        // -- Constructor -------------------------------------------------------
        public AnalysisStackComponent()
            : base(
                "AnalysisStack",
                "AStack",
                "Collects SpatialAnalysis objects and programs.\n" +
                "Programs may be raw ProgramDefinition objects or weighted programs\n" +
                "from the ValueSet component.\n" +
                "Scores every voxel for every program and assigns each voxel to its\n" +
                "best-matching program.\n\n" +
                "Assignment methods:\n" +
                "  0 = Highest score first (globally contested voxels resolved first)\n" +
                "  1 = Round-robin (each program gets its best voxel in turn)\n" +
                "  2 = Per program (program 0 fills first, then program 1, etc.)\n\n" +
                "core_indices:\n" +
                "  When one or more valid indices are supplied, those voxels are\n" +
                "  reserved as core before program assignment.\n" +
                "  Leave this input empty to run without a core.\n\n" +
                "show_unassigned:\n" +
                "  If True, unassigned voxels appear in the last output branch.\n" +
                "  If False, unassigned voxels are excluded from all outputs.\n\n" +
                "Version 1.2.0",
                "Cumulus",
                "3 | Main")
        { }

        // -- GUID — DO NOT CHANGE ----------------------------------------------
        public override Guid ComponentGuid =>
            new Guid("C3D4E5F6-A7B8-9012-CDEF-012345678908");

        // -- Icon --------------------------------------------------------------
        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "Cumulus.Resources.AnalysisStack_24.png");
                return stream != null ? new Bitmap(stream) : null!;
            }
        }

        // -- Parameters — order and nicknames unchanged ------------------------
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("voxel_grid", "VG",
                "VoxelGrid object from the VoxelGrid component.",
                GH_ParamAccess.item);

            pManager.AddGenericParameter("analysis", "A",
                "List of SpatialAnalysis objects from SA components.\n" +
                "Accepts a flat list or DataTree; flattened automatically.",
                GH_ParamAccess.tree);

            pManager.AddGenericParameter("programs", "P",
                "ProgramDefinition objects, or ProgramWithValueSet objects " +
                "from the ValueSet component.",
                GH_ParamAccess.list);

            pManager.AddBooleanParameter("show_all", "SA",
                "True  = all voxels assigned to a program.\n" +
                "False = clamp each program to its voxel_count.",
                GH_ParamAccess.item,
                true);

            pManager.AddIntegerParameter("method", "M",
                "Assignment method:\n" +
                "  0 = Highest score first\n" +
                "  1 = Round-robin\n" +
                "  2 = Per program",
                GH_ParamAccess.item,
                0);

            pManager.AddIntegerParameter("core_indices", "CI",
                "Optional core voxel indices from CoreLocation.\n" +
                "Supplying one or more valid indices enables core reservation.\n" +
                "Leave empty to run without a core.",
                GH_ParamAccess.list);

            pManager.AddBooleanParameter("show_unassigned", "SU",
                "True  = unassigned voxels output in the last branch.\n" +
                "False = unassigned voxels excluded from all outputs.",
                GH_ParamAccess.item,
                true);

            pManager[5].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("analysis_stack", "AS",
                "AnalysisStack object. Pass downstream.",
                GH_ParamAccess.item);
            pManager.AddIntegerParameter("program_indices", "PI",
                "DataTree of voxel indices per program.\n" +
                "Branch {p}         = voxel indices for program p.\n" +
                "Branch {n}         = core voxel indices (if core_indices are supplied).\n" +
                "Branch {n} or {n+1} = unassigned indices when show_unassigned=True.",
                GH_ParamAccess.tree);
            pManager.AddGeometryParameter("voxels", "V",
                "DataTree of voxel geometry parallel to program_indices.",
                GH_ParamAccess.tree);
            pManager.AddGenericParameter("shaders", "S",
                "DataTree of colors parallel to program_indices.\n" +
                "Alpha reflects per-program performance (255=best, 50=worst).\n" +
                "Core voxels = dark grey. Unassigned voxels = light grey A=40.",
                GH_ParamAccess.tree);
            pManager.AddTextParameter("info", "I",
                "Summary per program.",
                GH_ParamAccess.item);
        }

        // -- Solve -------------------------------------------------------------
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // -- Collect inputs ------------------------------------------------
            object? voxelGridObj = null;
            var analysisTree = new GH_Structure<IGH_Goo>();
            var programObjects = new List<object>();
            bool showAll = true;
            int method = 0;
            var coreIdxInput = new List<int>();
            bool showUnassigned = true;

            if (!DA.GetData(0, ref voxelGridObj)) return;
            if (!DA.GetDataTree(1, out analysisTree)) return;
            if (!DA.GetDataList(2, programObjects)) return;
            DA.GetData(3, ref showAll);
            DA.GetData(4, ref method);
            DA.GetDataList(5, coreIdxInput);
            DA.GetData(6, ref showUnassigned);

            method = Math.Max(0, Math.Min(2, method));

            // -- Unwrap VoxelGrid ----------------------------------------------
            var voxelGrid = voxelGridObj as VoxelGrid;
            if (voxelGrid == null && voxelGridObj is GH_ObjectWrapper vgw)
                voxelGrid = vgw.Value as VoxelGrid;

            if (voxelGrid == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Could not read VoxelGrid object.");
                return;
            }

            // -- Unwrap SpatialAnalysis objects --------------------------------
            var saList = new List<SpatialAnalysis>();
            foreach (var branch in analysisTree.Branches)
                foreach (var item in branch)
                {
                    var sa = UnwrapAnalysis(item);
                    if (sa != null) saList.Add(sa);
                }

            if (saList.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "No SpatialAnalysis objects found in 'analysis'.");
                return;
            }

            // -- Unwrap raw and ValueSet-enriched program inputs ----------------
            var progList = new List<ProgramDefinition>();
            var vsList = new List<ValueSet>();
            var seenProgramNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var obj in programObjects)
            {
                var coupledProgram = UnwrapProgramWithValueSet(obj);

                if (coupledProgram != null)
                {
                    if (!seenProgramNames.Add(coupledProgram.Program.Name))
                    {
                        AddRuntimeMessage(
                            GH_RuntimeMessageLevel.Warning,
                            $"Ignored duplicate program '{coupledProgram.Program.Name}'.");
                        continue;
                    }

                    progList.Add(coupledProgram.Program);
                    vsList.Add(coupledProgram.ValueSet);
                    continue;
                }

                var rawProgram = UnwrapProgram(obj);

                if (rawProgram == null)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        "Ignored an input that is neither ProgramDefinition nor ProgramWithValueSet.");
                    continue;
                }

                if (!seenProgramNames.Add(rawProgram.Name))
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        $"Ignored duplicate program '{rawProgram.Name}'.");
                    continue;
                }

                progList.Add(rawProgram);
            }

            if (progList.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "No ProgramDefinition or ProgramWithValueSet objects were found in P.");
                return;
            }

            // -- Call the Core engine ------------------------------------------
            // ProgramSpec carries only what scoring needs. Colour stays here,
            // because System.Drawing.Common is Windows-only from .NET 6 on and
            // must not be pulled into netstandard2.0 Core.
            int n = voxelGrid.FilledKeys.Count;

            var specs = progList
                .Select(p => new ProgramSpec(p.Name, p.VoxelCount))
                .ToList();

            var invalidCoreCount = coreIdxInput.Count(index => index < 0 || index >= n);

            var coreVoxels = coreIdxInput
                .Where(index => index >= 0 && index < n)
                .Distinct()
                .OrderBy(index => index)
                .ToList();

            bool useCore = coreVoxels.Count > 0;

            if (invalidCoreCount > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{invalidCoreCount} core index value(s) were outside the VoxelGrid and were ignored.");
            }

            ScoringResult result;
            try
            {
                result = VoxelScoringEngine.Run(
                    n,
                    saList,
                    specs,
                    vsList,
                    showAll,
                    (AssignmentMethod)method,
                    coreVoxels);
            }
            catch (ScoringInputException ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                return;
            }

            // -- Build DataTree outputs ----------------------------------------
            var idxTree = new GH_Structure<GH_Integer>();
            var voxelTree = new GH_Structure<IGH_GeometricGoo>();
            var shaderTree = new GH_Structure<GH_Colour>();

            var alphas = result.AllAlphas();
            int nPrograms = progList.Count;

            // Program branches {0 .. nPrograms-1}
            for (int p = 0; p < nPrograms; p++)
            {
                var path = new GH_Path(p);
                var progColor = progList[p].Color;

                foreach (var v in result.Ranked[p])
                {
                    var shader = Color.FromArgb(alphas[v],
                        progColor.R, progColor.G, progColor.B);

                    AppendVoxel(idxTree, voxelTree, shaderTree,
                                voxelGrid, v, shader, path);
                }
            }

            // -- Core branch {nPrograms} ---------------------------------------
            int coreBranchIdx = nPrograms;
            int unassignedBranchIdx = useCore ? nPrograms + 1 : nPrograms;

            var coreSet = new HashSet<int>(coreVoxels);

            if (useCore && coreSet.Count > 0)
            {
                var corePath = new GH_Path(coreBranchIdx);
                var coreColor = Color.FromArgb(180, 80, 80, 80); // dark grey

                foreach (var v in coreSet.OrderBy(v => v))
                    AppendVoxel(idxTree, voxelTree, shaderTree,
                                voxelGrid, v, coreColor, corePath);
            }

            // -- Unassigned branch ---------------------------------------------
            if (showUnassigned)
            {
                var unPath = new GH_Path(unassignedBranchIdx);
                var unassColor = Color.FromArgb(40, 160, 160, 160);

                for (int v = 0; v < n; v++)
                {
                    if (result.ProgramIndices[v] != -1) continue; // assigned or core
                    if (useCore && coreSet.Contains(v)) continue;

                    AppendVoxel(idxTree, voxelTree, shaderTree,
                                voxelGrid, v, unassColor, unPath);
                }
            }

            // -- Build AnalysisStackData ---------------------------------------
            var analysisStackData = new AnalysisStackData(
                voxelGrid,
                result.Labels,
                result.Channels,
                result.Raw,
                progList,
                result.ProgramIndices,
                result.WinningScore,
                result.Ranked);

            // -- Viewport preview ---------------------------------------------
            // Shader alpha remains in output S; viewport colors are opaque.
            BuildPreviewData(
                voxelGrid, result, progList, alphas,
                useCore, coreSet, showUnassigned);

            // -- Info ----------------------------------------------------------
            string info = BuildInfo(
                result, progList, method, useCore, showUnassigned,
                coreSet, coreBranchIdx, unassignedBranchIdx, n);

            // -- Output --------------------------------------------------------
            DA.SetData(0, analysisStackData);
            DA.SetDataTree(1, idxTree);
            DA.SetDataTree(2, voxelTree);
            DA.SetDataTree(3, shaderTree);
            DA.SetData(4, info);
        }

        // -- Viewport preview -------------------------------------------------

        private static Color ToOpaqueDisplayColor(Color color)
        {
            return Color.FromArgb(255, color.R, color.G, color.B);
        }

        private void BuildPreviewData(
            VoxelGrid voxelGrid,
            ScoringResult result,
            List<ProgramDefinition> programs,
            IReadOnlyList<int> alphas,
            bool useCore,
            HashSet<int> coreSet,
            bool showUnassigned)
        {
            _previewPoints.Clear();
            _previewBoxes.Clear();
            _previewColors.Clear();
            _previewAsBoxes = voxelGrid.ShowBoxes;

            for (int p = 0; p < programs.Count; p++)
            {
                var programColor = programs[p].Color;
                foreach (var voxelIndex in result.Ranked[p])
                {
                    var shaderColor = Color.FromArgb(
                        alphas[voxelIndex], programColor.R, programColor.G, programColor.B);
                    AddPreviewVoxel(voxelGrid, voxelIndex, shaderColor);
                }
            }

            if (useCore)
            {
                var coreColor = Color.FromArgb(180, 80, 80, 80);
                foreach (var voxelIndex in coreSet.OrderBy(index => index))
                    AddPreviewVoxel(voxelGrid, voxelIndex, coreColor);
            }

            if (showUnassigned)
            {
                var unassignedColor = Color.FromArgb(40, 160, 160, 160);
                for (int voxelIndex = 0; voxelIndex < result.ProgramIndices.Count; voxelIndex++)
                {
                    if (result.ProgramIndices[voxelIndex] != -1)
                        continue;
                    if (useCore && coreSet.Contains(voxelIndex))
                        continue;
                    AddPreviewVoxel(voxelGrid, voxelIndex, unassignedColor);
                }
            }
        }

        private void AddPreviewVoxel(VoxelGrid voxelGrid, int voxelIndex, Color shaderColor)
        {
            var key = voxelGrid.FilledKeys[voxelIndex];
            _previewColors.Add(ToOpaqueDisplayColor(shaderColor));

            if (_previewAsBoxes)
                _previewBoxes.Add(voxelGrid.KeyToBox(key));
            else
                _previewPoints.Add(voxelGrid.KeyToCenter(key));
        }

        public override void DrawViewportMeshes(IGH_PreviewArgs args)
        {
            if (Hidden || !IsPreviewCapable || !_previewAsBoxes)
                return;

            for (int i = 0; i < _previewBoxes.Count && i < _previewColors.Count; i++)
            {
                var brep = _previewBoxes[i].ToBrep();
                if (brep == null)
                    continue;

                try
                {
                    args.Display.DrawBrepShaded(
                        brep,
                        new Rhino.Display.DisplayMaterial(_previewColors[i]));
                }
                finally
                {
                    brep.Dispose();
                }
            }
        }

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            if (Hidden || !IsPreviewCapable)
                return;

            if (_previewAsBoxes)
            {
                for (int i = 0; i < _previewBoxes.Count && i < _previewColors.Count; i++)
                    args.Display.DrawBox(_previewBoxes[i], _previewColors[i]);
                return;
            }

            for (int i = 0; i < _previewPoints.Count && i < _previewColors.Count; i++)
            {
                args.Display.DrawPoint(
                    _previewPoints[i],
                    Rhino.Display.PointStyle.RoundSimple,
                    5,
                    _previewColors[i]);
            }
        }

        public override bool IsPreviewCapable => true;

        public override BoundingBox ClippingBox
        {
            get
            {
                var boundingBox = BoundingBox.Empty;

                if (_previewAsBoxes)
                {
                    foreach (var box in _previewBoxes)
                        boundingBox.Union(box.BoundingBox);
                }
                else
                {
                    foreach (var point in _previewPoints)
                        boundingBox.Union(point);
                }

                return boundingBox;
            }
        }

        // -- Unwrap helpers ----------------------------------------------------
        // Each accepts a raw GH object, a GH_ObjectWrapper, or a duck-typed
        // object from a GHPython/C# script component.

        private static SpatialAnalysis? UnwrapAnalysis(object obj)
        {
            var inner = obj is GH_ObjectWrapper w ? w.Value : obj;
            if (inner == null) return null;
            if (inner is SpatialAnalysis sa) return sa;

            try
            {
                dynamic d = inner;
                string? lbl = d.label?.ToString();
                var vals = d.values;
                if (string.IsNullOrWhiteSpace(lbl) || vals == null) return null;

                var vlist = new List<double>();
                foreach (var v in vals)
                    vlist.Add(Convert.ToDouble(v));
                return new SpatialAnalysis(lbl!, vlist);
            }
            catch { return null; }
        }

        private static ProgramWithValueSet? UnwrapProgramWithValueSet(object obj)
        {
            var inner = obj is GH_ObjectWrapper wrapper
                ? wrapper.Value
                : obj;

            return inner as ProgramWithValueSet;
        }

        private static ProgramDefinition? UnwrapProgram(object obj)
        {
            var inner = obj is GH_ObjectWrapper w ? w.Value : obj;
            if (inner == null) return null;
            if (inner is ProgramDefinition pd) return pd;

            try
            {
                dynamic dyn = inner;
                string? nm = dyn.name?.ToString();
                if (string.IsNullOrWhiteSpace(nm)) return null;

                int vc = Convert.ToInt32(dyn.voxel_count);
                dynamic dc = dyn.color;
                int r = Convert.ToInt32(dc.R);
                int g = Convert.ToInt32(dc.G);
                int b = Convert.ToInt32(dc.B);

                return new ProgramDefinition(nm!, Color.FromArgb(255, r, g, b), vc);
            }
            catch { return null; }
        }

        // -- Output helpers ----------------------------------------------------

        private static void AppendVoxel(
            GH_Structure<GH_Integer> idxTree,
            GH_Structure<IGH_GeometricGoo> voxelTree,
            GH_Structure<GH_Colour> shaderTree,
            VoxelGrid voxelGrid,
            int voxel,
            Color shader,
            GH_Path path)
        {
            var key = voxelGrid.FilledKeys[voxel];
            var geom = voxelGrid.KeyToGeometry(key);

            idxTree.Append(new GH_Integer(voxel), path);
            AppendGeometry(voxelTree, geom, path);
            shaderTree.Append(new GH_Colour(shader), path);
        }

        private static void AppendGeometry(
            GH_Structure<IGH_GeometricGoo> tree,
            GeometryBase geom,
            GH_Path path)
        {
            if (geom is Brep brep)
                tree.Append(new GH_Brep(brep), path);
            else if (geom is Rhino.Geometry.Point pt)
                tree.Append(new GH_Point(pt.Location), path);
            else if (geom is Mesh mesh)
                tree.Append(new GH_Mesh(mesh), path);
        }

        private static string BuildInfo(
            ScoringResult result,
            List<ProgramDefinition> progList,
            int method,
            bool useCore,
            bool showUnassigned,
            HashSet<int> coreSet,
            int coreBranchIdx,
            int unassignedBranchIdx,
            int n)
        {
            var lines = new System.Text.StringBuilder();

            lines.AppendLine(string.Format(
                "AnalysisStack v1.2.0 | voxels={0} | channels=[{1}] | programs=[{2}] | method={3}",
                n,
                string.Join(", ", result.Labels),
                string.Join(", ", progList.Select(p => p.Name)),
                method));
            lines.AppendLine("");
            lines.AppendLine("Channels (normalized):");

            foreach (var lbl in result.Labels)
            {
                var ch = result.Channels[lbl];
                var rw = result.Raw[lbl];
                lines.AppendLine(string.Format(
                    "  '{0}' | raw=[{1:F3} -> {2:F3}]  norm=[{3:F3} -> {4:F3}]",
                    lbl, rw.Min(), rw.Max(), ch.Min(), ch.Max()));
            }

            lines.AppendLine("");
            lines.AppendLine("Program assignments:");

            for (int p = 0; p < progList.Count; p++)
            {
                var prog = progList[p];
                string lim = prog.VoxelCount >= 0
                    ? prog.VoxelCount.ToString() : "unlimited";
                lines.AppendLine(string.Format(
                    "  [{0}] '{1}' | assigned={2} | voxel_count={3}",
                    p, prog.Name, result.Ranked[p].Count, lim));
            }

            if (useCore)
                lines.AppendLine(string.Format(
                    "\nCore voxels : {0} (branch {{{1}}})",
                    coreSet.Count, coreBranchIdx));

            int nUnassigned = Enumerable.Range(0, n)
                .Count(v => result.ProgramIndices[v] == -1 &&
                    !(useCore && coreSet.Contains(v)));

            if (showUnassigned)
                lines.AppendLine(string.Format(
                    "Unassigned  : {0} (branch {{{1}}})",
                    nUnassigned, unassignedBranchIdx));
            else
                lines.AppendLine(string.Format(
                    "Unassigned  : {0} (hidden — show_unassigned=False)",
                    nUnassigned));

            return lines.ToString().TrimEnd();
        }
    }

    // -- AnalysisStackData — downstream data container -------------------------
    // Stays in the Grasshopper layer: it holds a VoxelGrid, which is
    // RhinoCommon-dependent and therefore cannot live in Core.
    public class AnalysisStackData
    {
        public VoxelGrid VoxelGrid { get; }
        public List<string> Labels { get; }
        public Dictionary<string, List<double>> Channels { get; }
        public Dictionary<string, List<double>> Raw { get; }
        public List<ProgramDefinition> Programs { get; }
        public List<int> ProgramIndices { get; }
        public List<double> WinningScore { get; }
        public List<List<int>> Ranked { get; }
        public int NVoxels { get; }

        public AnalysisStackData(
            VoxelGrid voxelGrid,
            List<string> labels,
            Dictionary<string, List<double>> channels,
            Dictionary<string, List<double>> raw,
            List<ProgramDefinition> programs,
            List<int> programIndices,
            List<double> winningScore,
            List<List<int>> ranked)
        {
            VoxelGrid = voxelGrid;
            Labels = labels;
            Channels = channels;
            Raw = raw;
            Programs = programs;
            ProgramIndices = programIndices;
            WinningScore = winningScore;
            Ranked = ranked;
            NVoxels = voxelGrid.FilledKeys.Count;
        }

        public override string ToString()
        {
            return string.Format(
                "AnalysisStack(voxels={0}, channels=[{1}], programs=[{2}])",
                NVoxels,
                string.Join(", ", Labels),
                string.Join(", ", Programs.Select(p => p.Name)));
        }
    }
}

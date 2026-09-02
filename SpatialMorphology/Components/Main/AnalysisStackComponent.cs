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

namespace SpatialMorphology
{
    public class AnalysisStackComponent : GH_Component
    {
        // ── Constructor ───────────────────────────────────────────────────────
        public AnalysisStackComponent()
            : base(
                "AnalysisStack",
                "AStack",
                "Collects SpatialAnalysis objects, ProgramDefinitions, and ValueSets.\n" +
                "Scores every voxel for every program and assigns each voxel to its\n" +
                "best-matching program.\n\n" +
                "Assignment methods:\n" +
                "  0 = Highest score first (globally contested voxels resolved first)\n" +
                "  1 = Round-robin (each program gets its best voxel in turn)\n" +
                "  2 = Per program (program 0 fills first, then program 1, etc.)\n\n" +
                "use_core:\n" +
                "  If True and core_indices are connected, core voxels are extracted\n" +
                "  before program assignment and output in a dedicated branch.\n\n" +
                "show_unassigned:\n" +
                "  If True, unassigned voxels appear in the last output branch.\n" +
                "  If False, unassigned voxels are excluded from all outputs.\n\n" +
                "Version 1.2.0",
                "Spatial Morphology",
                "Main")
        { }

        // ── GUID ──────────────────────────────────────────────────────────────
        public override Guid ComponentGuid =>
            new Guid("C3D4E5F6-A7B8-9012-CDEF-012345678908");

        // ── Icon ──────────────────────────────────────────────────────────────
        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "SpatialMorphology.Resources.AnalysisStack_24.png");
                return stream != null ? new Bitmap(stream) : null;
            }
        }

        // ── Parameters ────────────────────────────────────────────────────────
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("voxel_grid", "VG",
                "VoxelGrid object from the VoxelGrid component.",
                GH_ParamAccess.item);
            pManager.AddGenericParameter("analysis", "A",
                "List of SpatialAnalysis objects from SA components.\n" +
                "Accepts flat list or DataTree — flattened automatically.",
                GH_ParamAccess.tree);
            pManager.AddGenericParameter("programs", "P",
                "List of ProgramDefinition objects.",
                GH_ParamAccess.list);
            pManager.AddGenericParameter("value_sets", "VS",
                "Optional. List of ValueSet objects from ValueSet component.\n" +
                "Matched to programs by ProgramName automatically.\n" +
                "Accepts flat list or DataTree — flattened automatically.",
                GH_ParamAccess.tree);
            pManager.AddBooleanParameter("show_all", "SA",
                "True  = all voxels assigned to a program.\n" +
                "False = clamp each program to its voxel_count.",
                GH_ParamAccess.item, true);
            pManager.AddIntegerParameter("method", "M",
                "Assignment method:\n" +
                "  0 = Highest score first\n" +
                "  1 = Round-robin\n" +
                "  2 = Per program",
                GH_ParamAccess.item, 0);
            pManager.AddBooleanParameter("use_core", "UC",
                "If True and core_indices are connected, core voxels are\n" +
                "reserved before program assignment and placed in their own\n" +
                "output branch {n_programs}.",
                GH_ParamAccess.item, false);
            pManager.AddIntegerParameter("core_indices", "CI",
                "Optional. Core voxel indices from the CoreLocation component.\n" +
                "Only used when use_core = True.",
                GH_ParamAccess.list);
            pManager.AddBooleanParameter("show_unassigned", "SU",
                "True  = unassigned voxels output in last branch.\n" +
                "False = unassigned voxels excluded from all outputs.",
                GH_ParamAccess.item, true);

            pManager[3].Optional = true;
            pManager[7].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("analysis_stack", "AS",
                "AnalysisStack object. Pass downstream.",
                GH_ParamAccess.item);
            pManager.AddIntegerParameter("program_indices", "PI",
                "DataTree of voxel indices per program.\n" +
                "Branch {p}         = voxel indices for program p.\n" +
                "Branch {n}         = core voxel indices (if use_core=True).\n" +
                "Branch {n} or {n+1} = unassigned indices (if show_unassigned=True).",
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

        // ── Solve ─────────────────────────────────────────────────────────────
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // ── Collect inputs ────────────────────────────────────────────────
            object voxelGridObj = null;
            var analysisTree = new GH_Structure<IGH_Goo>();
            var programObjects = new List<object>();
            var valueSetTree = new GH_Structure<IGH_Goo>();
            bool showAll = true;
            int method = 0;
            bool useCore = false;
            var coreIdxInput = new List<int>();
            bool showUnassigned = true;

            if (!DA.GetData(0, ref voxelGridObj)) return;
            if (!DA.GetDataTree(1, out analysisTree)) return;
            if (!DA.GetDataList(2, programObjects)) return;
            DA.GetDataTree(3, out valueSetTree);
            DA.GetData(4, ref showAll);
            DA.GetData(5, ref method);
            DA.GetData(6, ref useCore);
            DA.GetDataList(7, coreIdxInput);
            DA.GetData(8, ref showUnassigned);

            method = Math.Max(0, Math.Min(2, method));

            // ── Unwrap VoxelGrid ──────────────────────────────────────────────
            var voxelGrid = voxelGridObj as VoxelGrid;
            if (voxelGrid == null && voxelGridObj is GH_ObjectWrapper vgw)
                voxelGrid = vgw.Value as VoxelGrid;

            if (voxelGrid == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Could not read VoxelGrid object.");
                return;
            }

            // ── Flatten analysis DataTree ─────────────────────────────────────
            var analysisObjects = new List<object>();
            foreach (var branch in analysisTree.Branches)
                foreach (var item in branch)
                    analysisObjects.Add(item);

            // ── Flatten value_sets DataTree ───────────────────────────────────
            var valueSetObjects = new List<object>();
            foreach (var branch in valueSetTree.Branches)
                foreach (var item in branch)
                    valueSetObjects.Add(item);

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

            // ── Unwrap ProgramDefinition objects ──────────────────────────────
            var progList = new List<ProgramDefinition>();
            foreach (var obj in programObjects)
            {
                var inner = obj is GH_ObjectWrapper w ? w.Value : obj;
                if (inner is ProgramDefinition pd)
                {
                    progList.Add(pd);
                    continue;
                }
                if (inner != null)
                {
                    try
                    {
                        dynamic dyn = inner;
                        string nm = dyn.name?.ToString();
                        int vc = Convert.ToInt32(dyn.voxel_count);
                        dynamic dc = dyn.color;
                        int r = Convert.ToInt32(dc.R);
                        int g = Convert.ToInt32(dc.G);
                        int b = Convert.ToInt32(dc.B);
                        if (!string.IsNullOrWhiteSpace(nm))
                            progList.Add(new ProgramDefinition(
                                nm, Color.FromArgb(255, r, g, b), vc));
                    }
                    catch { }
                }
            }

            if (progList.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "No ProgramDefinition objects found in 'programs'.");
                return;
            }

            // ── Unwrap ValueSet objects ───────────────────────────────────────
            var vsList = new List<ValueSet>();
            foreach (var obj in valueSetObjects)
            {
                var inner = obj is GH_ObjectWrapper w ? w.Value : obj;
                if (inner is ValueSet vs)
                {
                    vsList.Add(vs);
                    continue;
                }
                if (inner != null)
                {
                    try
                    {
                        dynamic dyn = inner;
                        string pnm = dyn.program_name?.ToString();
                        dynamic wts = dyn.weights;
                        if (!string.IsNullOrWhiteSpace(pnm) && wts != null)
                        {
                            var wd = new Dictionary<string, double>();
                            foreach (var kvp in wts)
                                wd[kvp.Key.ToString()] = Convert.ToDouble(kvp.Value);
                            vsList.Add(new ValueSet(pnm, wd));
                        }
                    }
                    catch { }
                }
            }

            // ── Validate channel lengths ──────────────────────────────────────
            int n = voxelGrid.FilledKeys.Count;
            foreach (var sa in saList)
            {
                if (sa.Values.Count != n)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        string.Format(
                            "Channel '{0}' has {1} values but voxel_grid has {2} voxels.",
                            sa.Label, sa.Values.Count, n));
                    return;
                }
            }

            // ── Build core voxel set ──────────────────────────────────────────
            var coreSet = new HashSet<int>();
            if (useCore && coreIdxInput.Count > 0)
            {
                foreach (var idx in coreIdxInput)
                    if (idx >= 0 && idx < n)
                        coreSet.Add(idx);
            }

            // ── Step 1: Normalize SA channels ─────────────────────────────────
            var labels = new List<string>();
            var channels = new Dictionary<string, List<double>>();
            var raw = new Dictionary<string, List<double>>();
            var seen = new HashSet<string>();

            foreach (var sa in saList)
            {
                if (seen.Contains(sa.Label))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        string.Format("Duplicate channel label '{0}'.", sa.Label));
                    return;
                }

                var sanitized = new List<double>();
                foreach (var v in sa.Values)
                    sanitized.Add(double.IsNaN(v) || v == double.PositiveInfinity
                        ? 0.0 : v);

                double lo = sanitized.Min();
                double hi = sanitized.Max();
                double rng = hi - lo;

                var norm = rng > 1e-12
                    ? sanitized.Select(v => (v - lo) / rng).ToList()
                    : Enumerable.Repeat(0.0, n).ToList();

                seen.Add(sa.Label);
                labels.Add(sa.Label);
                raw[sa.Label] = sanitized;
                channels[sa.Label] = norm;
            }

            // ── Step 2: Build multiplier table from ValueSets ─────────────────
            var valueSetMap = new Dictionary<string, Dictionary<string, double>>();
            foreach (var vs in vsList)
                valueSetMap[vs.ProgramName] = vs.Weights;

            // ── Step 3: Score every voxel for every program ───────────────────
            int nPrograms = progList.Count;
            var rawScores = new List<List<double>>();

            for (int pIdx = 0; pIdx < nPrograms; pIdx++)
            {
                var prog = progList[pIdx];
                var weights = valueSetMap.ContainsKey(prog.Name)
                    ? valueSetMap[prog.Name] : null;
                var s = new List<double>(new double[n]);

                foreach (var lbl in labels)
                {
                    var ch = channels[lbl];
                    double m = (weights != null && weights.ContainsKey(lbl))
                        ? weights[lbl] : 1.0;

                    if (m == 0.0) continue;

                    if (m > 0)
                        for (int i = 0; i < n; i++)
                            s[i] += m * ch[i];
                    else
                    {
                        double absM = Math.Abs(m);
                        for (int i = 0; i < n; i++)
                            s[i] += absM * (1.0 - ch[i]);
                    }
                }
                rawScores.Add(s);
            }

            // ── Step 4: Assign voxels to programs ─────────────────────────────
            // Core voxels are excluded from program assignment
            var programIndices = new List<int>(new int[n]);
            var winningScore = new List<double>(new double[n]);
            var assigned = new HashSet<int>();

            // Pre-mark core voxels as reserved (-2)
            for (int i = 0; i < n; i++)
                programIndices[i] = -1;

            if (useCore)
                foreach (var idx in coreSet)
                    programIndices[idx] = -2; // reserved for core

            // Build eligible voxels (not core)
            var eligibleVoxels = Enumerable.Range(0, n)
                .Where(i => programIndices[i] != -2)
                .ToList();

            // Score eligible voxels
            if (method == 0)
            {
                // Method 0 — Highest score first globally
                var allScores = new List<(double score, int voxel, int program)>();
                foreach (var v in eligibleVoxels)
                    for (int p = 0; p < nPrograms; p++)
                        allScores.Add((rawScores[p][v], v, p));

                allScores.Sort((a, b) => b.score.CompareTo(a.score));

                var programCounts = new int[nPrograms];

                foreach (var (score, v, p) in allScores)
                {
                    if (assigned.Contains(v)) continue;

                    var prog = progList[p];
                    if (!showAll && prog.VoxelCount >= 0 &&
                        programCounts[p] >= prog.VoxelCount)
                        continue;

                    programIndices[v] = p;
                    winningScore[v] = score;
                    assigned.Add(v);
                    programCounts[p]++;

                    // Tie-break: prefer program with fewer assigned voxels
                    // already handled by processing in score order
                }
            }
            else if (method == 1)
            {
                // Method 1 — Round-robin
                var programCounts = new int[nPrograms];
                var programQueues = new List<Queue<(double score, int voxel)>>();

                for (int p = 0; p < nPrograms; p++)
                {
                    var sorted = eligibleVoxels
                        .Select(v => (rawScores[p][v], v))
                        .OrderByDescending(x => x.Item1)
                        .ToList();
                    programQueues.Add(new Queue<(double, int)>(sorted));
                }

                bool anyActive = true;
                while (anyActive)
                {
                    anyActive = false;
                    for (int p = 0; p < nPrograms; p++)
                    {
                        var prog = progList[p];
                        if (!showAll && prog.VoxelCount >= 0 &&
                            programCounts[p] >= prog.VoxelCount)
                            continue;

                        while (programQueues[p].Count > 0)
                        {
                            var (score, v) = programQueues[p].Dequeue();
                            if (assigned.Contains(v)) continue;

                            programIndices[v] = p;
                            winningScore[v] = score;
                            assigned.Add(v);
                            programCounts[p]++;
                            anyActive = true;
                            break;
                        }
                    }
                }
            }
            else
            {
                // Method 2 — Per program
                var programCounts = new int[nPrograms];

                for (int p = 0; p < nPrograms; p++)
                {
                    var prog = progList[p];
                    var sorted = eligibleVoxels
                        .Where(v => !assigned.Contains(v))
                        .OrderByDescending(v => rawScores[p][v])
                        .ToList();

                    foreach (var v in sorted)
                    {
                        if (!showAll && prog.VoxelCount >= 0 &&
                            programCounts[p] >= prog.VoxelCount)
                            break;

                        programIndices[v] = p;
                        winningScore[v] = rawScores[p][v];
                        assigned.Add(v);
                        programCounts[p]++;
                    }
                }
            }

            // ── Step 5: Rank voxels per program best→worst ────────────────────
            var ranked = new List<List<int>>();
            for (int p = 0; p < nPrograms; p++)
            {
                var pVoxels = Enumerable.Range(0, n)
                    .Where(v => programIndices[v] == p)
                    .OrderByDescending(v => winningScore[v])
                    .ToList();
                ranked.Add(pVoxels);
            }

            // ── Step 6: Global alpha mapping ──────────────────────────────────
            var assignedScores = Enumerable.Range(0, n)
                .Where(v => programIndices[v] >= 0)
                .Select(v => winningScore[v])
                .ToList();

            double gLo = assignedScores.Count > 0 ? assignedScores.Min() : 0.0;
            double gHi = assignedScores.Count > 0 ? assignedScores.Max() : 0.0;

            int AlphaFor(int v)
            {
                if (programIndices[v] < 0) return 40;
                double sc = winningScore[v];
                double t = gHi > gLo ? (sc - gLo) / (gHi - gLo) : 0.0;
                return Math.Max(50, Math.Min(255, (int)Math.Round(50 + t * 205)));
            }

            // ── Build DataTree outputs ────────────────────────────────────────
            var idxTree = new GH_Structure<GH_Integer>();
            var voxelTree = new GH_Structure<IGH_GeometricGoo>();
            var shaderTree = new GH_Structure<GH_Colour>();

            // ── Program branches {0..n_programs-1} ────────────────────────────
            for (int p = 0; p < nPrograms; p++)
            {
                var path = new GH_Path(p);
                var progColor = progList[p].Color;

                foreach (var v in ranked[p])
                {
                    int a = AlphaFor(v);
                    var shader = Color.FromArgb(a,
                        progColor.R, progColor.G, progColor.B);
                    var key = voxelGrid.FilledKeys[v];
                    var geom = voxelGrid.KeyToGeometry(key);

                    idxTree.Append(new GH_Integer(v), path);
                    AppendGeometry(voxelTree, geom, path);
                    shaderTree.Append(new GH_Colour(shader), path);
                }
            }

            // ── Core branch {n_programs} ──────────────────────────────────────
            int coreBranchIdx = nPrograms;
            int unassignedBranchIdx = useCore ? nPrograms + 1 : nPrograms;

            if (useCore && coreSet.Count > 0)
            {
                var corePath = new GH_Path(coreBranchIdx);
                var coreColor = Color.FromArgb(180, 80, 80, 80); // dark grey

                var sortedCore = coreSet.OrderBy(v => v).ToList();
                foreach (var v in sortedCore)
                {
                    var key = voxelGrid.FilledKeys[v];
                    var geom = voxelGrid.KeyToGeometry(key);

                    idxTree.Append(new GH_Integer(v), corePath);
                    AppendGeometry(voxelTree, geom, corePath);
                    shaderTree.Append(new GH_Colour(coreColor), corePath);
                }
            }

            // ── Unassigned branch ─────────────────────────────────────────────
            if (showUnassigned)
            {
                var unPath = new GH_Path(unassignedBranchIdx);
                var unassColor = Color.FromArgb(40, 160, 160, 160);

                for (int v = 0; v < n; v++)
                {
                    if (programIndices[v] != -1) continue; // assigned or core
                    if (useCore && coreSet.Contains(v)) continue;

                    var key = voxelGrid.FilledKeys[v];
                    var geom = voxelGrid.KeyToGeometry(key);

                    idxTree.Append(new GH_Integer(v), unPath);
                    AppendGeometry(voxelTree, geom, unPath);
                    shaderTree.Append(new GH_Colour(unassColor), unPath);
                }
            }

            // ── Build AnalysisStackData ───────────────────────────────────────
            var analysisStackData = new AnalysisStackData(
                voxelGrid, labels, channels, raw,
                progList, programIndices.ToList(),
                winningScore.ToList(), ranked);

            // ── Info ──────────────────────────────────────────────────────────
            var lines = new System.Text.StringBuilder();
            lines.AppendLine(string.Format(
                "AnalysisStack v1.2.0 | voxels={0} | channels=[{1}] | programs=[{2}] | method={3}",
                n,
                string.Join(", ", labels),
                string.Join(", ", progList.Select(p => p.Name)),
                method));
            lines.AppendLine("");
            lines.AppendLine("Channels (normalized):");

            foreach (var lbl in labels)
            {
                var ch = channels[lbl];
                var rw = raw[lbl];
                lines.AppendLine(string.Format(
                    "  '{0}' | raw=[{1:F3} -> {2:F3}]  norm=[{3:F3} -> {4:F3}]",
                    lbl, rw.Min(), rw.Max(), ch.Min(), ch.Max()));
            }

            lines.AppendLine("");
            lines.AppendLine("Program assignments:");

            for (int p = 0; p < nPrograms; p++)
            {
                var prog = progList[p];
                string lim = prog.VoxelCount >= 0
                    ? prog.VoxelCount.ToString() : "unlimited";
                lines.AppendLine(string.Format(
                    "  [{0}] '{1}' | assigned={2} | voxel_count={3}",
                    p, prog.Name, ranked[p].Count, lim));
            }

            if (useCore)
                lines.AppendLine(string.Format(
                    "\nCore voxels : {0} (branch {{{1}}})",
                    coreSet.Count, coreBranchIdx));

            int nUnassigned = Enumerable.Range(0, n)
                .Count(v => programIndices[v] == -1 &&
                    !(useCore && coreSet.Contains(v)));

            if (showUnassigned)
                lines.AppendLine(string.Format(
                    "Unassigned  : {0} (branch {{{1}}})",
                    nUnassigned, unassignedBranchIdx));
            else
                lines.AppendLine(string.Format(
                    "Unassigned  : {0} (hidden — show_unassigned=False)",
                    nUnassigned));

            string info = lines.ToString().TrimEnd();

            // ── Output ────────────────────────────────────────────────────────
            DA.SetData(0, analysisStackData);
            DA.SetDataTree(1, idxTree);
            DA.SetDataTree(2, voxelTree);
            DA.SetDataTree(3, shaderTree);
            DA.SetData(4, info);
        }

        // ── Geometry append helper ────────────────────────────────────────────
        private void AppendGeometry(
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
    }
    // ── AnalysisStackData — downstream data container ─────────────────────────
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
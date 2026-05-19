// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Grasshopper.Kernel;

namespace SpatialMorphology
{
    public class ValueSetComponent : GH_Component
    {
        // ── Stored weights ────────────────────────────────────────────────────
        // weights[programName][channelLabel] = multiplier
        private Dictionary<string, Dictionary<string, double>> _weights
            = new Dictionary<string, Dictionary<string, double>>();

        // ── Constructor ───────────────────────────────────────────────────────
        public ValueSetComponent()
            : base(
                "ValueSet",
                "ValSet",
                "Define per-channel multipliers for each program. Click 'Edit Weights' to open the matrix editor.",
                "Spatial Morphology",
                "Setup")
        { }

        // ── GUID ──────────────────────────────────────────────────────────────
        public override Guid ComponentGuid =>
            new Guid("B1C2D3E4-F5A6-7890-BCDE-F12345678901");

        // ── Parameters ────────────────────────────────────────────────────────
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("programs", "P",
                "List of ProgramDefinition objects from ProgramDefinition components.",
                GH_ParamAccess.list);
            pManager.AddGenericParameter("analysis", "A",
                "List of SpatialAnalysis objects from SA components.",
                GH_ParamAccess.list);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("value_sets", "VS",
                "List of ValueSet objects. Wire into AnalysisStack.",
                GH_ParamAccess.list);
            pManager.AddTextParameter("info", "I",
                "Summary of current weights.",
                GH_ParamAccess.item);
        }

        // ── Solve ─────────────────────────────────────────────────────────────

        protected override void SolveInstance(IGH_DataAccess DA)
        {

            // ── Collect inputs ────────────────────────────────────────────────
            var programObjects = new List<object>();
            var analysisObjects = new List<object>();

            if (!DA.GetDataList(0, programObjects)) return;
            if (!DA.GetDataList(1, analysisObjects)) return;

            // ── Debug — inspect incoming objects ─────────────────────────────────────────
            var debugInfo = new System.Text.StringBuilder();
            foreach (var obj in analysisObjects)
            {
                var inner = obj is Grasshopper.Kernel.Types.GH_ObjectWrapper w
                    ? w.Value : obj;
                if (inner == null) continue;

                try
                {
                    dynamic dynObj = inner;
                    string l = dynObj.label?.ToString();
                    if (!string.IsNullOrWhiteSpace(l) && !channelLabels.Contains(l))
                        channelLabels.Add(l);
                }
                catch { }
            }
            DA.SetData(1, debugInfo.ToString());
            return;

            // ── Extract program names ─────────────────────────────────────────
            var programNames = new List<string>();
            foreach (var obj in programObjects)
            {
                var inner = obj is Grasshopper.Kernel.Types.GH_ObjectWrapper w
                    ? w.Value : obj;
                var nameProp = inner?.GetType().GetProperty("name")
                            ?? inner?.GetType().GetProperty("Name");
                if (nameProp != null)
                {
                    var n = nameProp.GetValue(inner)?.ToString();
                    if (!string.IsNullOrWhiteSpace(n))
                        programNames.Add(n);
                }
            }

            // ── Extract channel labels ────────────────────────────────────────
            var channelLabels = new List<string>();
            foreach (var obj in analysisObjects)
            {
                var inner = obj is Grasshopper.Kernel.Types.GH_ObjectWrapper w
                    ? w.Value : obj;
                var labelProp = inner?.GetType().GetProperty("label")
                             ?? inner?.GetType().GetProperty("Label");
                if (labelProp != null)
                {
                    var l = labelProp.GetValue(inner)?.ToString();
                    if (!string.IsNullOrWhiteSpace(l) && !channelLabels.Contains(l))
                        channelLabels.Add(l);
                }
            }

            if (programNames.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "No program names found. Connect ProgramDefinition objects.");
                return;
            }
            if (channelLabels.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "No channel labels found. Connect SpatialAnalysis objects.");
                return;
            }

            // ── Sync weights dict with current programs/channels ──────────────
            // Add missing programs/channels with default weight 1.0
            foreach (var prog in programNames)
            {
                if (!_weights.ContainsKey(prog))
                    _weights[prog] = new Dictionary<string, double>();
                foreach (var ch in channelLabels)
                    if (!_weights[prog].ContainsKey(ch))
                        _weights[prog][ch] = 1.0;
            }

            // ── Build ValueSet outputs ────────────────────────────────────────
            var valueSets = new List<ValueSet>();
            foreach (var prog in programNames)
            {
                var ws = new Dictionary<string, double>();
                foreach (var ch in channelLabels)
                    ws[ch] = _weights.ContainsKey(prog) && _weights[prog].ContainsKey(ch)
                             ? _weights[prog][ch] : 1.0;
                valueSets.Add(new ValueSet(prog, ws));
            }

            // ── Info string ───────────────────────────────────────────────────
            var lines = new System.Text.StringBuilder();
            lines.AppendLine(string.Format(
                "ValueSet | {0} programs x {1} channels",
                programNames.Count, channelLabels.Count));
            lines.AppendLine("");

            // Header row
            lines.Append("Program".PadRight(20));
            foreach (var ch in channelLabels)
                lines.Append(ch.PadRight(14));
            lines.AppendLine("");

            // Divider
            lines.AppendLine(new string('-', 20 + channelLabels.Count * 14));

            // Data rows
            foreach (var prog in programNames)
            {
                lines.Append(prog.PadRight(20));
                foreach (var ch in channelLabels)
                {
                    var m = _weights.ContainsKey(prog) && _weights[prog].ContainsKey(ch)
                            ? _weights[prog][ch] : 1.0;
                    lines.Append(string.Format("{0:+0.00;-0.00}", m).PadRight(14));
                }
                lines.AppendLine("");
            }

            DA.SetDataList(0, valueSets);
            DA.SetData(1, lines.ToString().TrimEnd());
        }

        // ── Custom UI button ──────────────────────────────────────────────────
        public override void AppendAdditionalMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalMenuItems(menu);
            Menu_AppendItem(menu, "Edit Weights...", OnEditWeights, true);
        }

        private void OnEditWeights(object sender, EventArgs e)
        {
            // Collect current program names and channel labels from weights dict
            var programNames = _weights.Keys.ToList();
            var channelLabels = programNames.Count > 0
                ? _weights[programNames[0]].Keys.ToList()
                : new List<string>();

            if (programNames.Count == 0 || channelLabels.Count == 0)
            {
                MessageBox.Show(
                    "Connect programs and analysis inputs first, then run the component before editing weights.",
                    "ValueSet",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            // Build current weights array for the form
            int nP = programNames.Count;
            int nC = channelLabels.Count;
            var existing = new double[nP, nC];

            for (int p = 0; p < nP; p++)
                for (int c = 0; c < nC; c++)
                    existing[p, c] = _weights.ContainsKey(programNames[p]) &&
                                     _weights[programNames[p]].ContainsKey(channelLabels[c])
                                     ? _weights[programNames[p]][channelLabels[c]]
                                     : 1.0;

            // Open matrix form
            using (var form = new ValueSetMatrixForm(programNames, channelLabels, existing))
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    // Read weights back from form
                    double[,] newWeights = form.GetWeights();
                    for (int p = 0; p < nP; p++)
                    {
                        if (!_weights.ContainsKey(programNames[p]))
                            _weights[programNames[p]] = new Dictionary<string, double>();
                        for (int c = 0; c < nC; c++)
                            _weights[programNames[p]][channelLabels[c]] =
                                newWeights[p, c];
                    }

                    // Re-run the component to update outputs
                    ExpireSolution(true);
                }
            }
        }

        // ── Serialization — save/load weights with the GH file ────────────────
        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            int p = 0;
            foreach (var prog in _weights)
            {
                writer.SetString("prog_" + p, prog.Key);
                int c = 0;
                foreach (var ch in prog.Value)
                {
                    writer.SetString(string.Format("ch_{0}_{1}", p, c), ch.Key);
                    writer.SetDouble(string.Format("wt_{0}_{1}", p, c), ch.Value);
                    c++;
                }
                writer.SetInt32("ch_count_" + p, c);
                p++;
            }
            writer.SetInt32("prog_count", p);
            return base.Write(writer);
        }

        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            _weights = new Dictionary<string, Dictionary<string, double>>();
            int progCount = 0;
            if (reader.TryGetInt32("prog_count", ref progCount))
            {
                for (int p = 0; p < progCount; p++)
                {
                    string progName = "";
                    if (!reader.TryGetString("prog_" + p, ref progName)) continue;
                    _weights[progName] = new Dictionary<string, double>();

                    int chCount = 0;
                    reader.TryGetInt32("ch_count_" + p, ref chCount);
                    for (int c = 0; c < chCount; c++)
                    {
                        string chName = "";
                        double wt = 1.0;
                        if (reader.TryGetString(string.Format("ch_{0}_{1}", p, c), ref chName) &&
                            reader.TryGetDouble(string.Format("wt_{0}_{1}", p, c), ref wt))
                            _weights[progName][chName] = wt;
                    }
                }
            }
            return base.Read(reader);
        }
    }
}

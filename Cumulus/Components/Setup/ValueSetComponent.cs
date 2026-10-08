// -*- coding: utf-8 -*-
// Version 2.1.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using Grasshopper.Kernel;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;

namespace Cumulus
{
    /// <summary>
    /// Defines per-channel multipliers for each program. Double-click the component
    /// on the canvas to open the matrix editor.
    /// </summary>
    /// <remarks>
    /// CROSS-PLATFORM NOTE
    /// This file must compile and load on Rhino 8 for Windows AND macOS.
    /// That rules out System.Windows.Forms entirely: no MessageBox, no
    /// ToolStripDropDown, no AppendAdditionalMenuItems override. All UI goes
    /// through Eto.Forms or Rhino.UI, both of which exist on both platforms.
    ///
    /// Two separate pieces of state are kept on purpose:
    ///
    ///   _weights            persistent ARCHIVE of every multiplier ever set,
    ///                       keyed [program][channel]. Entries are never removed
    ///                       automatically, so disconnecting a channel and
    ///                       reconnecting it later restores the value the user
    ///                       chose rather than silently resetting it to +1.0.
    ///
    ///   _liveProgramNames   what is ACTUALLY connected right now, in input
    ///   _liveChannelLabels  order. Rebuilt from scratch on every solve.
    ///
    /// The editor and the value_sets output both read the LIVE lists. The archive
    /// is only consulted to look up a starting value. Reading the editor's rows
    /// from the archive was the original bug: removing an analysis input left the
    /// old key in the dictionary, so the matrix kept showing a column for a
    /// channel that was no longer wired in.
    /// </remarks>
    public class ValueSetComponent : GH_Component
    {
        // -- Persistent archive ------------------------------------------------
        // weights[programName][channelLabel] = multiplier
        private Dictionary<string, Dictionary<string, double>> _weights
            = new Dictionary<string, Dictionary<string, double>>();

        // -- Live state, rebuilt each solve ------------------------------------
        private List<string> _liveProgramNames = new List<string>();
        private List<string> _liveChannelLabels = new List<string>();
        private bool _hasSolved;

        // -- Constructor -------------------------------------------------------
        public ValueSetComponent()
            : base(
                "ValueSet",
                "ValSet",
                "Define per-channel multipliers for each program.\n" +
                "Double-click the component to open the matrix editor.\n\n" +
                "The editor always shows exactly the programs and channels that\n" +
                "are connected right now. Disconnecting an analysis removes its\n" +
                "column; reconnecting it restores the multiplier you had set.\n\n" +
                "Version 2.1.0",
                "Cumulus",
                "1 | Setup")
        { }

        // -- GUID — DO NOT CHANGE ----------------------------------------------
        public override Guid ComponentGuid =>
            new Guid("B1C2D3E4-F5A6-7890-BCDE-F12345678901");

        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "Cumulus.Resources.ValueSet_24.png");
                return stream != null ? new Bitmap(stream) : null!;
            }
        }

        // -- Custom attributes: double-click opens the editor ------------------
        public override void CreateAttributes()
        {
            m_attributes = new ValueSetAttributes(this);
        }

        private sealed class ValueSetAttributes : Grasshopper.Kernel.Attributes.GH_ComponentAttributes
        {
            public ValueSetAttributes(ValueSetComponent owner) : base(owner) { }

            public override GH_ObjectResponse RespondToMouseDoubleClick(
                GH_Canvas sender, GH_CanvasMouseEvent e)
            {
                ((ValueSetComponent)Owner).OpenWeightsEditor();
                return GH_ObjectResponse.Handled;
            }
        }

        // NOTE: no AppendAdditionalMenuItems override here. The Grasshopper
        // signature for it takes a System.Windows.Forms.ToolStripDropDown, which
        // does not exist in the macOS Rhino runtime. Archive housekeeping is
        // offered inside the Eto editor instead — see OpenWeightsEditor.

        // -- Parameters — order and nicknames unchanged -------------------------
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("analysis", "A",
                "List of SpatialAnalysis objects from SA components.\n" +
                "Removing one removes its column from the editor.",
                GH_ParamAccess.list);
            pManager.AddGenericParameter("programs", "P",
                "List of ProgramDefinition objects from ProgramDefinition components.",
                GH_ParamAccess.list);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("programs", "P",
                "ProgramDefinition objects coupled with their ValueSet weights. " +
                "Wire directly into AnalysisStack P.",
                GH_ParamAccess.list);
            pManager.AddTextParameter("info", "I",
                "Summary of current weights.",
                GH_ParamAccess.item);
        }

        // -- Solve -------------------------------------------------------------
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var programObjects = new List<object>();
            var analysisObjects = new List<object>();

            // Clear live state up front so a failed solve cannot leave the
            // editor showing channels from the previous successful run.
            _liveProgramNames = new List<string>();
            _liveChannelLabels = new List<string>();
            _hasSolved = false;

            if (!DA.GetDataList(0, analysisObjects)) return;
            if (!DA.GetDataList(1, programObjects)) return;

            // -- Extract complete programs, preserving input order --------------
            var programs = new List<ProgramDefinition>();
            var programNames = new List<string>();
            var seenProgramNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var obj in programObjects)
            {
                var program = UnwrapProgram(obj);

                if (program == null)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        "Ignored an input that is not a valid ProgramDefinition.");
                    continue;
                }

                if (!seenProgramNames.Add(program.Name))
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        $"Ignored duplicate program '{program.Name}'.");
                    continue;
                }

                programs.Add(program);
                programNames.Add(program.Name);
            }

            // -- Extract channel labels, preserving input order ----------------
            var channelLabels = new List<string>();
            foreach (var obj in analysisObjects)
            {
                var inner = obj is Grasshopper.Kernel.Types.GH_ObjectWrapper w2
                    ? w2.Value : obj;
                if (inner == null) continue;

                if (inner is SpatialAnalysis sa)
                {
                    if (!channelLabels.Contains(sa.Label))
                        channelLabels.Add(sa.Label);
                    continue;
                }
                try
                {
                    dynamic dynObj = inner;
                    string l = dynObj.label?.ToString();
                    if (!string.IsNullOrWhiteSpace(l) && !channelLabels.Contains(l))
                        channelLabels.Add(l);
                }
                catch { }
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

            // -- Commit live state ---------------------------------------------
            _liveProgramNames = programNames;
            _liveChannelLabels = channelLabels;
            _hasSolved = true;

            // -- Seed the archive for anything new -----------------------------
            // Existing entries are left alone. Nothing is ever deleted here:
            // that is what makes reconnecting a channel restore its multiplier.
            foreach (var prog in programNames)
            {
                if (!_weights.ContainsKey(prog))
                    _weights[prog] = new Dictionary<string, double>();

                foreach (var ch in channelLabels)
                    if (!_weights[prog].ContainsKey(ch))
                        _weights[prog][ch] = 1.0;
            }

            // -- Build coupled ProgramDefinition + ValueSet outputs -------------
            // AnalysisStack can consume these directly through its P input.
            var programsWithValueSets = new List<ProgramWithValueSet>();

            foreach (var program in programs)
            {
                var weights = new Dictionary<string, double>();

                foreach (var channel in channelLabels)
                    weights[channel] = LookupWeight(program.Name, channel);

                var valueSet = new ValueSet(program.Name, weights);

                programsWithValueSets.Add(
                    new ProgramWithValueSet(program, valueSet));
            }

            // -- Info string ---------------------------------------------------
            int archivedExtras = CountArchivedExtras();

            var lines = new System.Text.StringBuilder();
            lines.AppendLine(string.Format(
                "ValueSet | {0} programs x {1} channels",
                programNames.Count, channelLabels.Count));
            lines.AppendLine("");

            lines.Append("Program".PadRight(20));
            foreach (var ch in channelLabels)
                lines.Append(ch.PadRight(14));
            lines.AppendLine("");

            lines.AppendLine(new string('-', 20 + channelLabels.Count * 14));

            foreach (var prog in programNames)
            {
                lines.Append(prog.PadRight(20));
                foreach (var ch in channelLabels)
                    lines.Append(string.Format("{0:+0.00;-0.00}",
                        LookupWeight(prog, ch)).PadRight(14));
                lines.AppendLine("");
            }

            if (archivedExtras > 0)
            {
                lines.AppendLine("");
                lines.AppendLine(string.Format(
                    "{0} remembered multiplier(s) for disconnected channels are " +
                    "being held in reserve.", archivedExtras));
                lines.AppendLine(
                    "They do not affect the output. Reconnect a channel to get " +
                    "its value back, or clear them from the editor.");
            }

            DA.SetDataList(0, programsWithValueSets);
            DA.SetData(1, lines.ToString().TrimEnd());
        }

        // -- Program unwrapping -----------------------------------------------
        private static ProgramDefinition? UnwrapProgram(object obj)
        {
            var inner = obj is Grasshopper.Kernel.Types.GH_ObjectWrapper wrapper
                ? wrapper.Value
                : obj;

            if (inner == null)
                return null;

            if (inner is ProgramDefinition program)
                return program;

            try
            {
                dynamic value = inner;

                string? name = value.name?.ToString();
                if (string.IsNullOrWhiteSpace(name))
                    return null;

                int voxelCount = Convert.ToInt32(value.voxel_count);

                dynamic color = value.color;
                int red = Convert.ToInt32(color.R);
                int green = Convert.ToInt32(color.G);
                int blue = Convert.ToInt32(color.B);

                return new ProgramDefinition(
                    name,
                    Color.FromArgb(255, red, green, blue),
                    voxelCount);
            }
            catch
            {
                return null;
            }
        }

        // -- Archive lookup ----------------------------------------------------
        private double LookupWeight(string program, string channel)
        {
            return _weights.TryGetValue(program, out var row) &&
                   row.TryGetValue(channel, out double value)
                   ? value : 1.0;
        }

        /// <summary>
        /// Counts archived entries that no longer correspond to a connected
        /// program/channel pair. Purely informational.
        /// </summary>
        private int CountArchivedExtras()
        {
            if (!_hasSolved) return 0;

            int extras = 0;
            foreach (var prog in _weights)
            {
                bool progLive = _liveProgramNames.Contains(prog.Key);
                foreach (var ch in prog.Value)
                {
                    if (!progLive || !_liveChannelLabels.Contains(ch.Key))
                        extras++;
                }
            }
            return extras;
        }

        /// <summary>
        /// Drops archived entries for programs and channels that are not
        /// currently connected. Called from the editor, never automatically.
        /// </summary>
        private void PurgeArchive()
        {
            var cleaned = new Dictionary<string, Dictionary<string, double>>();

            foreach (var prog in _liveProgramNames)
            {
                var row = new Dictionary<string, double>();
                foreach (var ch in _liveChannelLabels)
                    row[ch] = LookupWeight(prog, ch);
                cleaned[prog] = row;
            }

            _weights = cleaned;
        }

        // -- Editor launch (called from double-click) ---------------------------
        internal void OpenWeightsEditor()
        {
            // Rhino.UI.Dialogs is available on Windows and macOS. MessageBox is not.
            if (!_hasSolved)
            {
                Rhino.UI.Dialogs.ShowMessage(
                    "Connect programs and analysis inputs, and let the component " +
                    "solve once, before editing weights.",
                    "ValueSet");
                return;
            }

            // Snapshot the LIVE lists. This is the whole fix: the editor is built
            // from what is connected now, not from the archive's leftover keys.
            var programNames = new List<string>(_liveProgramNames);
            var channelLabels = new List<string>(_liveChannelLabels);

            if (programNames.Count == 0 || channelLabels.Count == 0)
            {
                Rhino.UI.Dialogs.ShowMessage(
                    "Nothing to edit: connect at least one program and one analysis.",
                    "ValueSet");
                return;
            }

            int nP = programNames.Count;
            int nC = channelLabels.Count;
            var existing = new double[nP, nC];

            for (int p = 0; p < nP; p++)
                for (int c = 0; c < nC; c++)
                    existing[p, c] = LookupWeight(programNames[p], channelLabels[c]);

            var form = new UI.ValueSetMatrixForm(programNames, channelLabels, existing);
            form.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow);

            if (!form.Confirmed) return;

            double[,] newWeights = form.GetWeights();

            for (int p = 0; p < nP; p++)
            {
                if (!_weights.ContainsKey(programNames[p]))
                    _weights[programNames[p]] = new Dictionary<string, double>();

                for (int c = 0; c < nC; c++)
                    _weights[programNames[p]][channelLabels[c]] = newWeights[p, c];
            }

            // Offer archive housekeeping here rather than in a context menu,
            // since the context-menu API is Windows-only.
            int extras = CountArchivedExtras();
            if (extras > 0)
            {
                var answer = Rhino.UI.Dialogs.ShowMessage(
                    string.Format(
                        "{0} multiplier(s) are still remembered for programs or " +
                        "channels that are no longer connected.\n\n" +
                        "Keep them, so reconnecting restores your values?\n\n" +
                        "Yes  = keep (recommended)\n" +
                        "No   = forget them permanently",
                        extras),
                    "ValueSet",
                    Rhino.UI.ShowMessageButton.YesNo,
                    Rhino.UI.ShowMessageIcon.Question);

                if (answer == Rhino.UI.ShowMessageResult.No)
                    PurgeArchive();
            }

            ExpireSolution(true);
        }

        // -- Serialization — original keys preserved, do not rename -------------
        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetInt32("schema", 2);

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

            // Persist the live lists so a reopened file shows the correct matrix
            // even if the user double-clicks before the first solve finishes.
            writer.SetInt32("live_prog_count", _liveProgramNames.Count);
            for (int i = 0; i < _liveProgramNames.Count; i++)
                writer.SetString("live_prog_" + i, _liveProgramNames[i]);

            writer.SetInt32("live_ch_count", _liveChannelLabels.Count);
            for (int i = 0; i < _liveChannelLabels.Count; i++)
                writer.SetString("live_ch_" + i, _liveChannelLabels[i]);

            return base.Write(writer);
        }

        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            _weights = new Dictionary<string, Dictionary<string, double>>();
            _liveProgramNames = new List<string>();
            _liveChannelLabels = new List<string>();
            _hasSolved = false;

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

            // Schema 1 files have no live lists; they stay empty and the first
            // solve repopulates them.
            int liveProgCount = 0;
            if (reader.TryGetInt32("live_prog_count", ref liveProgCount))
            {
                for (int i = 0; i < liveProgCount; i++)
                {
                    string n = "";
                    if (reader.TryGetString("live_prog_" + i, ref n))
                        _liveProgramNames.Add(n);
                }
            }

            int liveChCount = 0;
            if (reader.TryGetInt32("live_ch_count", ref liveChCount))
            {
                for (int i = 0; i < liveChCount; i++)
                {
                    string n = "";
                    if (reader.TryGetString("live_ch_" + i, ref n))
                        _liveChannelLabels.Add(n);
                }
            }

            _hasSolved = _liveProgramNames.Count > 0 && _liveChannelLabels.Count > 0;

            return base.Read(reader);
        }
    }
}

// -*- coding: utf-8 -*-
// Version 2.2.0
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
    public class ValueSetComponent : GH_Component
    {
        // weights[programName][channelLabel] = multiplier
        private Dictionary<string, Dictionary<string, double>> _weights
            = new Dictionary<string, Dictionary<string, double>>();

        // Live input state; rebuilt on every successful solve.
        private List<string> _liveProgramNames = new List<string>();
        private List<string> _liveChannelLabels = new List<string>();
        private bool _hasSolved;

        public ValueSetComponent()
            : base(
                "ValueSet",
                "ValSet",
                "Defines per-channel multipliers for each program.\n" +
                "Double-click the component to open the interactive weight matrix.\n\n" +
                "The editor provides numeric fields, sliders, seeded Randomize and " +
                "Jitter controls. Enable Live Update to recompute Grasshopper while editing.\n\n" +
                "Version 2.2.0",
                "Cumulus",
                "1 | Setup")
        {
        }

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

        public override void CreateAttributes()
        {
            m_attributes = new ValueSetAttributes(this);
        }

        private sealed class ValueSetAttributes
            : Grasshopper.Kernel.Attributes.GH_ComponentAttributes
        {
            public ValueSetAttributes(ValueSetComponent owner) : base(owner)
            {
            }

            public override GH_ObjectResponse RespondToMouseDoubleClick(
                GH_Canvas sender,
                GH_CanvasMouseEvent e)
            {
                ((ValueSetComponent)Owner).OpenWeightsEditor();
                return GH_ObjectResponse.Handled;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "analysis",
                "A",
                "SpatialAnalysis objects. Their labels become the matrix columns.",
                GH_ParamAccess.list);

            pManager.AddGenericParameter(
                "programs",
                "P",
                "ProgramDefinition objects. Their names become the matrix rows.",
                GH_ParamAccess.list);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "programs",
                "P",
                "ProgramDefinition objects coupled with their ValueSet weights. " +
                "Wire directly into AnalysisStack P.",
                GH_ParamAccess.list);

            pManager.AddTextParameter(
                "info",
                "I",
                "Summary of active weights.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var programObjects = new List<object>();
            var analysisObjects = new List<object>();

            _liveProgramNames = new List<string>();
            _liveChannelLabels = new List<string>();
            _hasSolved = false;

            if (!DA.GetDataList(0, analysisObjects))
                return;

            if (!DA.GetDataList(1, programObjects))
                return;

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
                        "Ignored duplicate program '" + program.Name + "'.");
                    continue;
                }

                programs.Add(program);
                programNames.Add(program.Name);
            }

            var channelLabels = new List<string>();
            foreach (var obj in analysisObjects)
            {
                var inner = obj is Grasshopper.Kernel.Types.GH_ObjectWrapper wrapper
                    ? wrapper.Value
                    : obj;

                if (inner == null)
                    continue;

                if (inner is SpatialAnalysis analysis)
                {
                    if (!channelLabels.Contains(analysis.Label))
                        channelLabels.Add(analysis.Label);
                    continue;
                }

                try
                {
                    dynamic value = inner;
                    string label = value.label?.ToString();

                    if (!string.IsNullOrWhiteSpace(label) &&
                        !channelLabels.Contains(label))
                    {
                        channelLabels.Add(label);
                    }
                }
                catch
                {
                    // Invalid generic input is ignored; the live summary exposes
                    // only valid analysis labels.
                }
            }

            if (programNames.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "No valid ProgramDefinition objects found. Connect programs to P.");
                return;
            }

            if (channelLabels.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "No SpatialAnalysis labels found. Connect analyses to A.");
                return;
            }

            _liveProgramNames = programNames;
            _liveChannelLabels = channelLabels;
            _hasSolved = true;

            EnsureWeights(programNames, channelLabels);

            var programsWithValueSets = new List<ProgramWithValueSet>();

            foreach (var program in programs)
            {
                var row = new Dictionary<string, double>();

                foreach (var channel in channelLabels)
                    row[channel] = LookupWeight(program.Name, channel);

                programsWithValueSets.Add(
                    new ProgramWithValueSet(
                        program,
                        new ValueSet(program.Name, row)));
            }

            int archivedExtras = CountArchivedExtras();

            var lines = new System.Text.StringBuilder();
            lines.AppendLine(string.Format(
                "ValueSet | {0} programs x {1} channels",
                programNames.Count,
                channelLabels.Count));
            lines.AppendLine("");
            lines.Append("Program".PadRight(20));

            foreach (var channel in channelLabels)
                lines.Append(channel.PadRight(14));

            lines.AppendLine("");
            lines.AppendLine(new string('-', 20 + channelLabels.Count * 14));

            foreach (var program in programNames)
            {
                lines.Append(program.PadRight(20));

                foreach (var channel in channelLabels)
                {
                    lines.Append(
                        LookupWeight(program, channel)
                            .ToString("+0.00;-0.00;0.00")
                            .PadRight(14));
                }

                lines.AppendLine("");
            }

            if (archivedExtras > 0)
            {
                lines.AppendLine("");
                lines.AppendLine(string.Format(
                    "{0} remembered multiplier(s) for disconnected programs or channels " +
                    "are retained in the archive.",
                    archivedExtras));
            }

            DA.SetDataList(0, programsWithValueSets);
            DA.SetData(1, lines.ToString().TrimEnd());
        }

        /// <summary>
        /// Applies a live Eto-editor matrix to the current persistent archive.
        /// Values are clamped to the supported interactive range.
        /// </summary>
        internal void ApplyLiveWeights(
            IReadOnlyList<string> programNames,
            IReadOnlyList<string> channelLabels,
            double[,] weights)
        {
            if (programNames == null)
                throw new ArgumentNullException(nameof(programNames));

            if (channelLabels == null)
                throw new ArgumentNullException(nameof(channelLabels));

            if (weights == null)
                throw new ArgumentNullException(nameof(weights));

            for (int programIndex = 0; programIndex < programNames.Count; programIndex++)
            {
                string program = programNames[programIndex];

                if (!_weights.TryGetValue(program, out var row))
                {
                    row = new Dictionary<string, double>();
                    _weights[program] = row;
                }

                for (int channelIndex = 0; channelIndex < channelLabels.Count; channelIndex++)
                {
                    if (programIndex >= weights.GetLength(0) ||
                        channelIndex >= weights.GetLength(1))
                    {
                        continue;
                    }

                    row[channelLabels[channelIndex]] = ClampWeight(
                        weights[programIndex, channelIndex]);
                }
            }

            ExpireSolution(true);
        }

        private void EnsureWeights(
            IEnumerable<string> programNames,
            IEnumerable<string> channelLabels)
        {
            foreach (var program in programNames)
            {
                if (!_weights.TryGetValue(program, out var row))
                {
                    row = new Dictionary<string, double>();
                    _weights[program] = row;
                }

                foreach (var channel in channelLabels)
                {
                    if (!row.ContainsKey(channel))
                        row[channel] = 1.0;
                }
            }
        }

        private static double ClampWeight(double value)
        {
            return Math.Max(-1.0, Math.Min(1.0, value));
        }

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

                return new ProgramDefinition(
                    name,
                    Color.FromArgb(
                        255,
                        Convert.ToInt32(color.R),
                        Convert.ToInt32(color.G),
                        Convert.ToInt32(color.B)),
                    voxelCount);
            }
            catch
            {
                return null;
            }
        }

        private double LookupWeight(string program, string channel)
        {
            return _weights.TryGetValue(program, out var row) &&
                   row.TryGetValue(channel, out double value)
                ? value
                : 1.0;
        }

        private int CountArchivedExtras()
        {
            if (!_hasSolved)
                return 0;

            int extras = 0;

            foreach (var program in _weights)
            {
                bool programIsLive = _liveProgramNames.Contains(program.Key);

                foreach (var channel in program.Value)
                {
                    if (!programIsLive ||
                        !_liveChannelLabels.Contains(channel.Key))
                    {
                        extras++;
                    }
                }
            }

            return extras;
        }

        private void PurgeArchive()
        {
            var cleaned = new Dictionary<string, Dictionary<string, double>>();

            foreach (var program in _liveProgramNames)
            {
                var row = new Dictionary<string, double>();

                foreach (var channel in _liveChannelLabels)
                    row[channel] = LookupWeight(program, channel);

                cleaned[program] = row;
            }

            _weights = cleaned;
        }

        internal void OpenWeightsEditor()
        {
            if (!_hasSolved)
            {
                Rhino.UI.Dialogs.ShowMessage(
                    "Connect programs and analysis inputs, then allow ValueSet " +
                    "to solve once before opening the editor.",
                    "ValueSet");
                return;
            }

            var programNames = new List<string>(_liveProgramNames);
            var channelLabels = new List<string>(_liveChannelLabels);

            if (programNames.Count == 0 || channelLabels.Count == 0)
            {
                Rhino.UI.Dialogs.ShowMessage(
                    "Nothing to edit: connect at least one program and one analysis.",
                    "ValueSet");
                return;
            }

            var existing = new double[programNames.Count, channelLabels.Count];

            for (int p = 0; p < programNames.Count; p++)
            {
                for (int c = 0; c < channelLabels.Count; c++)
                    existing[p, c] = LookupWeight(programNames[p], channelLabels[c]);
            }

            var form = new UI.ValueSetMatrixForm(
                programNames,
                channelLabels,
                existing,
                liveWeights => ApplyLiveWeights(programNames, channelLabels, liveWeights));

            form.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow);

            if (!form.Confirmed)
                return;

            ApplyLiveWeights(programNames, channelLabels, form.GetWeights());

            int extras = CountArchivedExtras();

            if (extras <= 0)
                return;

            var answer = Rhino.UI.Dialogs.ShowMessage(
                string.Format(
                    "{0} multiplier(s) are remembered for disconnected programs or channels.\n\n" +
                    "Keep them so reconnecting restores the values?\n\n" +
                    "Yes = keep them (recommended)\n" +
                    "No = forget them permanently",
                    extras),
                "ValueSet",
                Rhino.UI.ShowMessageButton.YesNo,
                Rhino.UI.ShowMessageIcon.Question);

            if (answer == Rhino.UI.ShowMessageResult.No)
            {
                PurgeArchive();
                ExpireSolution(true);
            }
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetInt32("schema", 2);

            int programIndex = 0;

            foreach (var program in _weights)
            {
                writer.SetString("prog_" + programIndex, program.Key);

                int channelIndex = 0;

                foreach (var channel in program.Value)
                {
                    writer.SetString(
                        string.Format("ch_{0}_{1}", programIndex, channelIndex),
                        channel.Key);

                    writer.SetDouble(
                        string.Format("wt_{0}_{1}", programIndex, channelIndex),
                        channel.Value);

                    channelIndex++;
                }

                writer.SetInt32("ch_count_" + programIndex, channelIndex);
                programIndex++;
            }

            writer.SetInt32("prog_count", programIndex);

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

            int programCount = 0;

            if (reader.TryGetInt32("prog_count", ref programCount))
            {
                for (int p = 0; p < programCount; p++)
                {
                    string programName = "";

                    if (!reader.TryGetString("prog_" + p, ref programName))
                        continue;

                    _weights[programName] = new Dictionary<string, double>();

                    int channelCount = 0;
                    reader.TryGetInt32("ch_count_" + p, ref channelCount);

                    for (int c = 0; c < channelCount; c++)
                    {
                        string channelName = "";
                        double weight = 1.0;

                        if (reader.TryGetString(
                                string.Format("ch_{0}_{1}", p, c),
                                ref channelName) &&
                            reader.TryGetDouble(
                                string.Format("wt_{0}_{1}", p, c),
                                ref weight))
                        {
                            _weights[programName][channelName] = weight;
                        }
                    }
                }
            }

            int liveProgramCount = 0;

            if (reader.TryGetInt32("live_prog_count", ref liveProgramCount))
            {
                for (int i = 0; i < liveProgramCount; i++)
                {
                    string name = "";

                    if (reader.TryGetString("live_prog_" + i, ref name))
                        _liveProgramNames.Add(name);
                }
            }

            int liveChannelCount = 0;

            if (reader.TryGetInt32("live_ch_count", ref liveChannelCount))
            {
                for (int i = 0; i < liveChannelCount; i++)
                {
                    string name = "";

                    if (reader.TryGetString("live_ch_" + i, ref name))
                        _liveChannelLabels.Add(name);
                }
            }

            _hasSolved =
                _liveProgramNames.Count > 0 &&
                _liveChannelLabels.Count > 0;

            return base.Read(reader);
        }
    }
}

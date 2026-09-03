// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using Eto.Drawing;
using Eto.Forms;

namespace SpatialMorphology.UI
{
    /// <summary>
    /// Cross-platform (Eto.Forms) replacement for the WinForms DataGridView
    /// weight-matrix editor. Runs on Rhino 8 for both Windows and macOS.
    /// </summary>
    /// <remarks>
    /// Public contract is unchanged from the WinForms version: construct with
    /// (programNames, channelLabels, existingWeights), show it modally, then
    /// check <see cref="Confirmed"/> and read <see cref="GetWeights"/>.
    /// <para>
    /// The per-cell heat-map colouring from the WinForms version has been
    /// removed deliberately. Eto has no independently addressable cells, so it
    /// had to be produced from the <c>CellFormatting</c> event, which runs
    /// inside the platform draw loop — an exception there terminates the host
    /// process (Rhino) instead of surfacing as a managed error. The sign of a
    /// weight is still readable because values are formatted with an explicit
    /// +/- prefix.
    /// </para>
    /// </remarks>
    public class ValueSetMatrixForm : Dialog
    {
        // ── Private fields ────────────────────────────────────────────────────
        private readonly List<string> _programNames;
        private readonly List<string> _channelLabels;
        private readonly ObservableCollection<WeightRow> _rows;
        private bool _confirmed;

        /// <summary>One grid row: a program name plus its channel multipliers.</summary>
        private sealed class WeightRow
        {
            public string Program { get; }

            /// <summary>Multipliers for this program, indexed by channel.</summary>
            public double[] Values { get; }

            public WeightRow(string program, double[] values)
            {
                Program = program;
                Values = values;
            }

            public string Get(int channel)
            {
                if (channel < 0 || channel >= Values.Length) return string.Empty;
                return Values[channel].ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
            }

            public void Set(int channel, string? text)
            {
                if (channel < 0 || channel >= Values.Length) return;
                if (string.IsNullOrWhiteSpace(text)) return;

                if (double.TryParse(text.Trim(),
                                    NumberStyles.Float,
                                    CultureInfo.InvariantCulture,
                                    out double parsed))
                {
                    Values[channel] = parsed;
                }
                // Unparseable input is ignored, which replaces the WinForms
                // DataError handler. The grid re-reads the getter and the old
                // value reappears.
            }
        }

        // ── Constructor ───────────────────────────────────────────────────────
        /// <summary>Creates the editor.</summary>
        /// <param name="programNames">Row labels, in order.</param>
        /// <param name="channelLabels">Column headers, in order.</param>
        /// <param name="existingWeights">Current multipliers, indexed [program, channel]. May be null.</param>
        public ValueSetMatrixForm(
            List<string> programNames,
            List<string> channelLabels,
            double[,] existingWeights)
        {
            _programNames = programNames ?? throw new ArgumentNullException(nameof(programNames));
            _channelLabels = channelLabels ?? throw new ArgumentNullException(nameof(channelLabels));

            int nP = _programNames.Count;
            int nC = _channelLabels.Count;

            // ── Rows ──────────────────────────────────────────────────────────
            _rows = new ObservableCollection<WeightRow>();
            for (int p = 0; p < nP; p++)
            {
                var values = new double[nC];
                for (int c = 0; c < nC; c++)
                {
                    values[c] = (existingWeights != null &&
                                 existingWeights.GetLength(0) > p &&
                                 existingWeights.GetLength(1) > c)
                                ? existingWeights[p, c]
                                : 1.0;
                }
                _rows.Add(new WeightRow(_programNames[p], values));
            }

            // ── Dialog setup ──────────────────────────────────────────────────
            // No Segoe UI: that font does not exist on macOS. Use the system default.
            Title = "ValueSet - Program x Channel Weights";
            Padding = new Padding(8);
            Resizable = true;

            // ── Instructions ──────────────────────────────────────────────────
            var label = new Label
            {
                Text = "Set multipliers for each program x channel pair.\n" +
                       "+1.0 = prefer HIGH   |   -1.0 = prefer LOW   |   0.0 = ignore"
            };

            // ── Grid ──────────────────────────────────────────────────────────
            // Eto has no row headers, so the program name becomes a read-only
            // first column instead.
            var grid = new GridView
            {
                DataStore = _rows,
                ShowHeader = true,
                GridLines = GridLines.Both,
                AllowMultipleSelection = false,
                RowHeight = 24
            };

            grid.Columns.Add(new GridColumn
            {
                HeaderText = "Program",
                Editable = false,
                Width = 130,
                Resizable = true,
                DataCell = new TextBoxCell
                {
                    Binding = Binding.Delegate<WeightRow, string>(r => r.Program)
                }
            });

            for (int c = 0; c < nC; c++)
            {
                int channel = c;   // capture per iteration

                grid.Columns.Add(new GridColumn
                {
                    HeaderText = _channelLabels[c],
                    Editable = true,
                    Width = 90,
                    Resizable = true,
                    DataCell = new TextBoxCell
                    {
                        Binding = Binding.Delegate<WeightRow, string>(
                            r => r.Get(channel),
                            (r, v) => r.Set(channel, v))
                    }
                });
            }

            // ── Buttons ───────────────────────────────────────────────────────
            var applyButton = new Button { Text = "Apply", Width = 80 };
            applyButton.Click += OnApplyClick;

            var cancelButton = new Button { Text = "Cancel", Width = 80 };
            cancelButton.Click += OnCancelClick;

            var resetButton = new Button { Text = "Reset to 1.0", Width = 100 };
            resetButton.Click += OnResetClick;

            DefaultButton = applyButton;
            AbortButton = cancelButton;

            var buttonRow = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Items =
                {
                    applyButton,
                    cancelButton,
                    resetButton,
                    new StackLayoutItem(null, expand: true)
                }
            };

            // ── Layout ────────────────────────────────────────────────────────
            Content = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 8,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    label,
                    new StackLayoutItem(grid, expand: true),
                    buttonRow
                }
            };

            int width = Math.Max(420, 150 + nC * 90);
            int height = Math.Max(240, 150 + nP * 24);
            ClientSize = new Size(Math.Min(width, 1200), Math.Min(height, 800));

            // Kept as a field only so Reset can refresh it.
            _grid = grid;
        }

        private readonly GridView _grid;

        // ── Event handlers ────────────────────────────────────────────────────
        private void OnApplyClick(object? sender, EventArgs e)
        {
            _confirmed = true;
            Close();
        }

        private void OnCancelClick(object? sender, EventArgs e)
        {
            _confirmed = false;
            Close();
        }

        private void OnResetClick(object? sender, EventArgs e)
        {
            foreach (var row in _rows)
                for (int c = 0; c < row.Values.Length; c++)
                    row.Values[c] = 1.0;

            // Re-assigning the data store is the safe way to force a full
            // refresh; calling Invalidate() during an active edit can re-enter
            // the draw loop.
            _grid.DataStore = _rows;
        }

        // ── Public accessors ──────────────────────────────────────────────────
        /// <summary>True if the user clicked Apply (not Cancel).</summary>
        public bool Confirmed => _confirmed;

        /// <summary>Returns the full weights matrix, indexed [program, channel].</summary>
        public double[,] GetWeights()
        {
            int nP = _programNames.Count;
            int nC = _channelLabels.Count;
            var result = new double[nP, nC];

            for (int p = 0; p < nP && p < _rows.Count; p++)
                for (int c = 0; c < nC; c++)
                    result[p, c] = _rows[p].Values[c];

            return result;
        }
    }
}

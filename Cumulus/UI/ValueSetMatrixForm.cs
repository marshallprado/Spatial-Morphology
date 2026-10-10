// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using Eto.Drawing;
using Eto.Forms;

namespace Cumulus.UI
{
    /// <summary>
    /// Cross-platform Eto matrix editor for ValueSet weights.
    /// Each weight has a numeric field and a slider in the range [-1.00, 1.00].
    /// Randomize and Jitter operations are seeded for repeatability.
    /// </summary>
    public class ValueSetMatrixForm : Dialog
    {
        private const double MinimumWeight = -1.0;
        private const double MaximumWeight = 1.0;
        private const double WeightStep = 0.05;

        private readonly List<string> _programNames;
        private readonly List<string> _channelLabels;
        private readonly double[,] _initialWeights;
        private readonly double[,] _weights;
        private readonly Action<double[,]>? _liveUpdate;
        private readonly List<WeightCellEditor> _editors =
            new List<WeightCellEditor>();

        private readonly CheckBox _liveUpdateCheckBox;
        private readonly NumericStepper _seedStepper;
        private readonly NumericStepper _jitterStepper;
        private readonly DropDown _programSelector;
        private readonly Label _statusLabel;
        private bool _confirmed;
        private bool _suppressLiveUpdate;

        private sealed class WeightCellEditor
        {
            private readonly double[,] _weights;
            private readonly int _programIndex;
            private readonly int _channelIndex;
            private readonly Action _changed;
            private bool _updating;

            public NumericStepper Numeric { get; }
            public Slider Slider { get; }

            public WeightCellEditor(
                double[,] weights,
                int programIndex,
                int channelIndex,
                Action changed)
            {
                _weights = weights;
                _programIndex = programIndex;
                _channelIndex = channelIndex;
                _changed = changed;

                Numeric = new NumericStepper
                {
                    MinValue = MinimumWeight,
                    MaxValue = MaximumWeight,
                    Increment = WeightStep,
                    DecimalPlaces = 2,
                    Width = 78,
                    Value = Clamp(weights[programIndex, channelIndex])
                };

                Slider = new Slider
                {
                    MinValue = -20,
                    MaxValue = 20,
                    Value = ToSliderValue(weights[programIndex, channelIndex]),
                    Width = 110
                };

                Numeric.ValueChanged += OnNumericValueChanged;
                Slider.ValueChanged += OnSliderValueChanged;
            }

            public Control CreateControl()
            {
                return new StackLayout
                {
                    Orientation = Orientation.Vertical,
                    Spacing = 2,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Items =
                    {
                        Numeric,
                        Slider
                    }
                };
            }

            public void SetValue(double value)
            {
                value = Quantize(value);

                _updating = true;
                _weights[_programIndex, _channelIndex] = value;
                Numeric.Value = value;
                Slider.Value = ToSliderValue(value);
                _updating = false;
            }

            private void OnNumericValueChanged(object? sender, EventArgs e)
            {
                if (_updating)
                    return;

                SetValue(Numeric.Value);
                _changed();
            }

            private void OnSliderValueChanged(object? sender, EventArgs e)
            {
                if (_updating)
                    return;

                SetValue(FromSliderValue(Slider.Value));
                _changed();
            }

            private static int ToSliderValue(double value)
            {
                return (int)Math.Round(
                    Clamp(value) / WeightStep,
                    MidpointRounding.AwayFromZero);
            }

            private static double FromSliderValue(int value)
            {
                return Quantize(value * WeightStep);
            }
        }

        /// <summary>
        /// Creates the interactive matrix editor.
        /// </summary>
        /// <param name="programNames">Row labels in stable input order.</param>
        /// <param name="channelLabels">Column labels in stable input order.</param>
        /// <param name="existingWeights">Initial values indexed [program, channel].</param>
        /// <param name="liveUpdate">
        /// Callback invoked with a copy of the current matrix when Live Update is enabled.
        /// </param>
        public ValueSetMatrixForm(
            List<string> programNames,
            List<string> channelLabels,
            double[,] existingWeights,
            Action<double[,]>? liveUpdate = null)
        {
            _programNames = programNames ?? throw new ArgumentNullException(nameof(programNames));
            _channelLabels = channelLabels ?? throw new ArgumentNullException(nameof(channelLabels));
            _liveUpdate = liveUpdate;

            int programCount = _programNames.Count;
            int channelCount = _channelLabels.Count;

            _initialWeights = new double[programCount, channelCount];
            _weights = new double[programCount, channelCount];

            for (int p = 0; p < programCount; p++)
            {
                for (int c = 0; c < channelCount; c++)
                {
                    double value =
                        existingWeights != null &&
                        existingWeights.GetLength(0) > p &&
                        existingWeights.GetLength(1) > c
                            ? existingWeights[p, c]
                            : 1.0;

                    value = Quantize(value);
                    _initialWeights[p, c] = value;
                    _weights[p, c] = value;
                }
            }

            Title = "ValueSet - Interactive Program x Channel Weights";
            Padding = new Padding(10);
            Resizable = true;

            var instructions = new Label
            {
                Text =
                    "Each cell is constrained to -1.00 through +1.00.\n" +
                    "+1.00 = prefer HIGH  |  -1.00 = prefer LOW  |  0.00 = ignore.\n" +
                    "Live Update recomputes Grasshopper while you edit. Cancel restores " +
                    "the matrix that existed when this dialog opened."
            };

            _liveUpdateCheckBox = new CheckBox
            {
                Text = "Live Update",
                Checked = false,
                ToolTip = "When enabled, slider, numeric, Randomize, Jitter, and Reset " +
                          "changes immediately recompute the Grasshopper definition."
            };

            _seedStepper = new NumericStepper
            {
                MinValue = 0,
                MaxValue = int.MaxValue,
                Increment = 1,
                DecimalPlaces = 0,
                Value = 1234,
                Width = 100
            };

            _jitterStepper = new NumericStepper
            {
                MinValue = 0.0,
                MaxValue = 1.0,
                Increment = WeightStep,
                DecimalPlaces = 2,
                Value = 0.25,
                Width = 82
            };

            _programSelector = new DropDown
            {
                DataStore = _programNames,
                SelectedIndex = _programNames.Count > 0 ? 0 : -1,
                Width = 180
            };

            _statusLabel = new Label
            {
                Text = "Editing all programs. Live Update is off.",
                TextColor = Colors.DimGray
            };

            var controlsRow = CreateControlsRow();
            var matrix = CreateMatrixLayout();
            var scrollable = new Scrollable
            {
                Content = matrix,
                Border = BorderType.Bezel,
                ExpandContentWidth = false,
                ExpandContentHeight = false
            };

            var applyButton = new Button { Text = "Apply", Width = 86 };
            applyButton.Click += OnApplyClick;

            var cancelButton = new Button { Text = "Cancel", Width = 86 };
            cancelButton.Click += OnCancelClick;

            DefaultButton = applyButton;
            AbortButton = cancelButton;

            var bottomRow = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Items =
                {
                    _statusLabel,
                    new StackLayoutItem(null, expand: true),
                    applyButton,
                    cancelButton
                }
            };

            Content = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 9,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    instructions,
                    controlsRow,
                    new StackLayoutItem(scrollable, expand: true),
                    bottomRow
                }
            };

            int width = Math.Max(700, Math.Min(1300, 180 + channelCount * 125));
            int height = Math.Max(360, Math.Min(850, 240 + programCount * 90));
            ClientSize = new Size(width, height);
        }

        /// <summary>True only after the user clicks Apply.</summary>
        public bool Confirmed => _confirmed;

        /// <summary>Returns a copy of the current matrix indexed [program, channel].</summary>
        public double[,] GetWeights()
        {
            return CopyMatrix(_weights);
        }

        private Control CreateControlsRow()
        {
            var randomizeAll = new Button { Text = "Randomize All" };
            randomizeAll.Click += (sender, e) =>
                ApplyRandomization(EnumerableProgramIndices(), false);

            var jitterAll = new Button { Text = "Jitter All" };
            jitterAll.Click += (sender, e) =>
                ApplyRandomization(EnumerableProgramIndices(), true);

            var resetAll = new Button { Text = "Reset All to +1.00" };
            resetAll.Click += (sender, e) =>
                ApplyReset(EnumerableProgramIndices());

            var randomizeRow = new Button { Text = "Randomize Row" };
            randomizeRow.Click += (sender, e) =>
                ApplyRandomization(SelectedProgramIndex(), false);

            var jitterRow = new Button { Text = "Jitter Row" };
            jitterRow.Click += (sender, e) =>
                ApplyRandomization(SelectedProgramIndex(), true);

            var resetRow = new Button { Text = "Reset Row" };
            resetRow.Click += (sender, e) =>
                ApplyReset(SelectedProgramIndex());

            return new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 5,
                Items =
                {
                    new StackLayout
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Items =
                        {
                            _liveUpdateCheckBox,
                            new Label { Text = "Seed:" },
                            _seedStepper,
                            new Label { Text = "Jitter:" },
                            _jitterStepper,
                            new Label { Text = "Selected program:" },
                            _programSelector,
                            new StackLayoutItem(null, expand: true)
                        }
                    },
                    new StackLayout
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Items =
                        {
                            randomizeAll,
                            jitterAll,
                            resetAll,
                            new Panel { Width = 8 },
                            randomizeRow,
                            jitterRow,
                            resetRow,
                            new StackLayoutItem(null, expand: true)
                        }
                    }
                }
            };
        }

        private TableLayout CreateMatrixLayout()
        {
            var table = new TableLayout
            {
                Spacing = new Size(5, 5),
                Padding = new Padding(6)
            };

            var headerCells = new List<TableCell>
            {
                new TableCell(new Label
                {
                    Text = "Program",
                    Font = SystemFonts.Bold(),
                    VerticalAlignment = VerticalAlignment.Center
                })
            };

            for (int c = 0; c < _channelLabels.Count; c++)
            {
                headerCells.Add(new TableCell(new Label
                {
                    Text = _channelLabels[c],
                    Font = SystemFonts.Bold(),
                    TextAlignment = TextAlignment.Center,
                    Width = 115,
                    Wrap = WrapMode.Word
                }));
            }

            table.Rows.Add(new TableRow(headerCells));

            for (int p = 0; p < _programNames.Count; p++)
            {
                var rowCells = new List<TableCell>
                {
                    new TableCell(new Label
                    {
                        Text = _programNames[p],
                        Width = 145,
                        VerticalAlignment = VerticalAlignment.Center,
                        Wrap = WrapMode.Word
                    })
                };

                for (int c = 0; c < _channelLabels.Count; c++)
                {
                    int programIndex = p;
                    int channelIndex = c;

                    var editor = new WeightCellEditor(
                        _weights,
                        programIndex,
                        channelIndex,
                        OnMatrixChanged);

                    _editors.Add(editor);
                    rowCells.Add(new TableCell(editor.CreateControl()));
                }

                table.Rows.Add(new TableRow(rowCells));
            }

            return table;
        }

        private void OnMatrixChanged()
        {
            if (_suppressLiveUpdate)
                return;

            if (_liveUpdateCheckBox.Checked == true)
            {
                _statusLabel.Text = "Live Update active.";
                _statusLabel.TextColor = Colors.DarkGreen;
                _liveUpdate?.Invoke(CopyMatrix(_weights));
            }
            else
            {
                _statusLabel.Text = "Edited locally. Click Apply to commit.";
                _statusLabel.TextColor = Colors.DimGray;
            }
        }

        private IEnumerable<int> EnumerableProgramIndices()
        {
            for (int p = 0; p < _programNames.Count; p++)
                yield return p;
        }

        private IEnumerable<int> SelectedProgramIndex()
        {
            if (_programSelector.SelectedIndex >= 0 &&
                _programSelector.SelectedIndex < _programNames.Count)
            {
                yield return _programSelector.SelectedIndex;
            }
        }

        private void ApplyRandomization(IEnumerable<int> programIndices, bool jitter)
        {
            var random = new Random((int)_seedStepper.Value);
            double amplitude = Clamp(_jitterStepper.Value);

            _suppressLiveUpdate = true;

            foreach (int p in programIndices)
            {
                if (p < 0 || p >= _programNames.Count)
                    continue;

                for (int c = 0; c < _channelLabels.Count; c++)
                {
                    double next = jitter
                        ? _weights[p, c] + (random.NextDouble() * 2.0 - 1.0) * amplitude
                        : random.NextDouble() * 2.0 - 1.0;

                    SetWeight(p, c, next);
                }
            }

            _suppressLiveUpdate = false;

            _statusLabel.Text = jitter
                ? "Jitter complete. Seed can reproduce this variation."
                : "Randomize complete. Seed can reproduce this matrix.";
            _statusLabel.TextColor = Colors.DimGray;

            OnMatrixChanged();
        }

        private void ApplyReset(IEnumerable<int> programIndices)
        {
            _suppressLiveUpdate = true;

            foreach (int p in programIndices)
            {
                if (p < 0 || p >= _programNames.Count)
                    continue;

                for (int c = 0; c < _channelLabels.Count; c++)
                    SetWeight(p, c, 1.0);
            }

            _suppressLiveUpdate = false;
            _statusLabel.Text = "Selected weights reset to +1.00.";
            _statusLabel.TextColor = Colors.DimGray;

            OnMatrixChanged();
        }

        private void SetWeight(int programIndex, int channelIndex, double value)
        {
            value = Quantize(value);
            _weights[programIndex, channelIndex] = value;

            int editorIndex = programIndex * _channelLabels.Count + channelIndex;

            if (editorIndex >= 0 && editorIndex < _editors.Count)
                _editors[editorIndex].SetValue(value);
        }

        private void OnApplyClick(object? sender, EventArgs e)
        {
            _confirmed = true;
            Close();
        }

        private void OnCancelClick(object? sender, EventArgs e)
        {
            if (_liveUpdateCheckBox.Checked == true)
                _liveUpdate?.Invoke(CopyMatrix(_initialWeights));

            _confirmed = false;
            Close();
        }

        private static double[,] CopyMatrix(double[,] source)
        {
            int rows = source.GetLength(0);
            int columns = source.GetLength(1);
            var copy = new double[rows, columns];

            for (int r = 0; r < rows; r++)
                for (int c = 0; c < columns; c++)
                    copy[r, c] = source[r, c];

            return copy;
        }

        private static double Clamp(double value)
        {
            return Math.Max(MinimumWeight, Math.Min(MaximumWeight, value));
        }

        private static double Quantize(double value)
        {
            return Math.Round(
                Clamp(value) / WeightStep,
                MidpointRounding.AwayFromZero) * WeightStep;
        }
    }
}

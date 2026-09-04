// -*- coding: utf-8 -*-
// Version 1.0.0
using System;
using System.Collections.Generic;

namespace SpatialMorphology
{
    /// <summary>
    /// Lightweight container pairing a channel label with per-voxel values.
    /// Parallel to VoxelGrid.FilledKeys.
    /// Normalization is performed by AnalysisStack.
    /// </summary>
    public class SpatialAnalysis
    {
        // ── Properties ────────────────────────────────────────────────────────
        public string Label { get; private set; }
        public List<double> Values { get; private set; }

        // ── Constructor ───────────────────────────────────────────────────────
        public SpatialAnalysis(string label, List<double> values)
        {
            if (string.IsNullOrWhiteSpace(label))
                throw new ArgumentException("label must be a non-empty string.");
            if (values == null)
                throw new ArgumentNullException("values");

            Label = label.Trim();
            Values = new List<double>(values);
        }

        // ── Representation ────────────────────────────────────────────────────
        public override string ToString()
        {
            int n = Values.Count;
            double lo = n > 0 ? Values[0] : 0;
            double hi = n > 0 ? Values[0] : 0;

            foreach (var v in Values)
            {
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }

            return string.Format(
                "SpatialAnalysis(label='{0}', n={1}, min={2:F3}, max={3:F3})",
                Label, n, lo, hi);
        }
    }
}
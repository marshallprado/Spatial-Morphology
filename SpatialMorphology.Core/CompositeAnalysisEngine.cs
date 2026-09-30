// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;

namespace SpatialMorphology
{
    /// <summary>
    /// How several normalised analysis channels are fused into one composite value.
    /// Integer values are part of the component's public <c>mode</c> input and must not change.
    /// </summary>
    public enum CompositeMode
    {
        /// <summary>
        /// 0 — weighted mean of the oriented channels. Multiplier magnitude scales
        /// each channel's contribution. Good general-purpose blend.
        /// </summary>
        WeightedSum = 0,

        /// <summary>
        /// 1 — the worst oriented channel wins (conjunctive: "good only if good in
        /// every respect"). Multiplier magnitude is ignored; only the sign is used.
        /// </summary>
        Minimum = 1,

        /// <summary>
        /// 2 — the best oriented channel wins (disjunctive: "good if good in any
        /// respect"). Multiplier magnitude is ignored; only the sign is used.
        /// </summary>
        Maximum = 2
    }

    /// <summary>Raised when the caller supplies inconsistent composite input.</summary>
    public class CompositeInputException : Exception
    {
        /// <summary>Creates the exception with a caller-facing message.</summary>
        public CompositeInputException(string message) : base(message) { }
    }

    /// <summary>
    /// Fuses several <see cref="SpatialAnalysis"/> channels into a single derived
    /// channel, so an abstract quality such as "privacy" or "prospect" can be built
    /// from concrete measurements and then used exactly like any other analysis.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Normalisation is mandatory and happens first.</b> Each input channel is
    /// independently mapped to 0..1 before any combination. Raw analysis channels
    /// live on wildly different scales — a distance channel might span 0..40 model
    /// units while a view channel spans 0..1200 rays — and summing raw values would
    /// let the larger-magnitude channel dominate regardless of the multiplier the
    /// user set. Normalising first is what makes the multipliers mean anything.
    /// </para>
    /// <para>
    /// <b>Sign convention matches ValueSet exactly.</b> A positive multiplier prefers
    /// high values, a negative multiplier prefers low values, and zero excludes the
    /// channel. This is deliberate: "privacy = view(-1) + proximity(-1)" reads the
    /// same way here as it does in a ValueSet, so users learn one rule rather than two.
    /// </para>
    /// <para>
    /// <b>Magnitude applies to WeightedSum only.</b> In Minimum and Maximum modes the
    /// magnitude is ignored and only the sign is honoured, because scaling a value
    /// before taking a min or max would change which channel wins for reasons
    /// unrelated to the data. <c>-2.0</c> and <c>-1.0</c> behave identically there.
    /// </para>
    /// <para>
    /// Output is always in 0..1. WeightedSum divides by the total absolute weight so
    /// the result is a weighted mean rather than an unbounded sum; this keeps the
    /// three modes on a comparable footing.
    /// </para>
    /// </remarks>
    public static class CompositeAnalysisEngine
    {
        private const double RangeEpsilon = 1e-12;

        /// <summary>
        /// Combines analysis channels into one derived channel.
        /// </summary>
        /// <param name="label">Label for the resulting channel, e.g. "privacy".</param>
        /// <param name="analyses">Input channels. All must have the same value count.</param>
        /// <param name="multipliers">
        /// Signed multiplier per channel, parallel to <paramref name="analyses"/>.
        /// If shorter than the analysis list, missing entries default to <c>1.0</c>.
        /// A multiplier of exactly <c>0.0</c> excludes that channel entirely.
        /// </param>
        /// <param name="mode">Combination strategy.</param>
        /// <returns>A single channel whose values lie in 0..1.</returns>
        /// <exception cref="CompositeInputException">
        /// No channels supplied, channel lengths differ, a channel is empty, or every
        /// supplied multiplier is zero.
        /// </exception>
        public static SpatialAnalysis Run(
            string label,
            IList<SpatialAnalysis> analyses,
            IList<double> multipliers,
            CompositeMode mode)
        {
            if (string.IsNullOrWhiteSpace(label))
                throw new CompositeInputException("label must be a non-empty string.");

            if (analyses == null || analyses.Count == 0)
                throw new CompositeInputException(
                    "At least one analysis channel is required.");

            // ── Validate parallel lengths ─────────────────────────────────────
            int n = analyses[0].Values.Count;
            if (n == 0)
                throw new CompositeInputException(
                    string.Format("Channel '{0}' contains no values.", analyses[0].Label));

            for (int i = 1; i < analyses.Count; i++)
            {
                if (analyses[i].Values.Count != n)
                    throw new CompositeInputException(string.Format(
                        "Channel '{0}' has {1} values but channel '{2}' has {3}. " +
                        "All inputs must describe the same voxel grid.",
                        analyses[i].Label, analyses[i].Values.Count,
                        analyses[0].Label, n));
            }

            // ── Resolve multipliers, defaulting missing entries to 1.0 ────────
            var weights = new double[analyses.Count];
            for (int i = 0; i < analyses.Count; i++)
            {
                double m = (multipliers != null && i < multipliers.Count)
                    ? multipliers[i]
                    : 1.0;
                weights[i] = Sanitise(m);
            }

            // ── Keep only contributing channels ──────────────────────────────
            var active = new List<int>();
            for (int i = 0; i < analyses.Count; i++)
                if (weights[i] != 0.0)
                    active.Add(i);

            if (active.Count == 0)
                throw new CompositeInputException(
                    "Every multiplier is 0.0, so no channel contributes. " +
                    "Set at least one multiplier to a non-zero value.");

            // ── Normalise each active channel to 0..1, then orient by sign ────
            // oriented[k][v] is "how good voxel v is on channel k", 0 = worst.
            var oriented = new List<double[]>(active.Count);
            foreach (int i in active)
            {
                double[] norm = Normalise(analyses[i].Values, n);

                if (weights[i] < 0.0)
                    for (int v = 0; v < n; v++)
                        norm[v] = 1.0 - norm[v];

                oriented.Add(norm);
            }

            // ── Combine ───────────────────────────────────────────────────────
            var result = new List<double>(n);

            switch (mode)
            {
                case CompositeMode.WeightedSum:
                    {
                        double totalWeight = active.Sum(i => Math.Abs(weights[i]));

                        for (int v = 0; v < n; v++)
                        {
                            double acc = 0.0;
                            for (int k = 0; k < active.Count; k++)
                                acc += Math.Abs(weights[active[k]]) * oriented[k][v];

                            result.Add(acc / totalWeight);
                        }
                        break;
                    }

                case CompositeMode.Minimum:
                    {
                        for (int v = 0; v < n; v++)
                        {
                            double lo = oriented[0][v];
                            for (int k = 1; k < oriented.Count; k++)
                                if (oriented[k][v] < lo) lo = oriented[k][v];

                            result.Add(lo);
                        }
                        break;
                    }

                case CompositeMode.Maximum:
                    {
                        for (int v = 0; v < n; v++)
                        {
                            double hi = oriented[0][v];
                            for (int k = 1; k < oriented.Count; k++)
                                if (oriented[k][v] > hi) hi = oriented[k][v];

                            result.Add(hi);
                        }
                        break;
                    }

                default:
                    throw new CompositeInputException(
                        "Unknown composite mode: " + (int)mode);
            }

            return new SpatialAnalysis(label.Trim(), result);
        }

        /// <summary>
        /// Maps values to 0..1. A channel whose range is below
        /// <see cref="RangeEpsilon"/> normalises to all zeros rather than dividing
        /// by zero — matching the behaviour of the scoring engine.
        /// </summary>
        private static double[] Normalise(IList<double> values, int n)
        {
            var clean = new double[n];
            for (int v = 0; v < n; v++)
                clean[v] = Sanitise(values[v]);

            double lo = clean[0];
            double hi = clean[0];
            for (int v = 1; v < n; v++)
            {
                if (clean[v] < lo) lo = clean[v];
                if (clean[v] > hi) hi = clean[v];
            }

            double range = hi - lo;
            if (range <= RangeEpsilon)
                return new double[n];   // all zeros

            var norm = new double[n];
            for (int v = 0; v < n; v++)
                norm[v] = (clean[v] - lo) / range;

            return norm;
        }

        /// <summary>
        /// Replaces NaN and both infinities with 0.0. Note this is stricter than the
        /// scoring engine, which currently lets NegativeInfinity through — a latent
        /// bug there, not a behaviour worth reproducing here.
        /// </summary>
        private static double Sanitise(double value)
        {
            return (double.IsNaN(value) || double.IsInfinity(value)) ? 0.0 : value;
        }
    }
}

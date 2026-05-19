// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;

namespace SpatialMorphology
{
    /// <summary>
    /// Stores signed multipliers linking a program to analysis channels.
    /// One ValueSet per program — consumed by AnalysisStack for voxel scoring.
    /// 
    /// Multiplier rules:
    ///   +1.0  = prefer HIGH values in this channel
    ///   -1.0  = prefer LOW values in this channel
    ///    0.0  = ignore this channel for this program
    ///    2.0  = prefer HIGH, twice as important as 1.0
    /// </summary>
    public class ValueSet
    {
        // ── Properties ────────────────────────────────────────────────────────

        /// <summary>Name of the program this ValueSet belongs to.</summary>
        public string ProgramName { get; private set; }

        /// <summary>
        /// Dictionary of channel label -> signed multiplier.
        /// Keys match SA component label outputs exactly.
        /// </summary>
        public Dictionary<string, double> Weights { get; private set; }

        // ── Construction ──────────────────────────────────────────────────────

        /// <summary>
        /// Create a ValueSet for a named program with a set of channel weights.
        /// </summary>
        /// <param name="programName">Must match a ProgramDefinition name exactly.</param>
        /// <param name="weights">Channel label to signed multiplier mapping.</param>
        public ValueSet(string programName, Dictionary<string, double> weights)
        {
            if (string.IsNullOrWhiteSpace(programName))
                throw new ArgumentException("programName must be a non-empty string.");
            if (weights == null)
                throw new ArgumentNullException("weights");

            ProgramName = programName.Trim();
            Weights = new Dictionary<string, double>(weights);
        }

        /// <summary>
        /// Create a ValueSet from parallel lists of labels and multipliers.
        /// </summary>
        /// <param name="programName">Must match a ProgramDefinition name exactly.</param>
        /// <param name="labels">Channel labels — must match SA component labels.</param>
        /// <param name="multipliers">Signed multipliers — parallel to labels.</param>
        public ValueSet(string programName, IList<string> labels, IList<double> multipliers)
        {
            if (string.IsNullOrWhiteSpace(programName))
                throw new ArgumentException("programName must be a non-empty string.");
            if (labels == null)
                throw new ArgumentNullException("labels");
            if (multipliers == null)
                throw new ArgumentNullException("multipliers");
            if (labels.Count != multipliers.Count)
                throw new ArgumentException(string.Format(
                    "labels ({0}) and multipliers ({1}) must have the same length.",
                    labels.Count, multipliers.Count));

            ProgramName = programName.Trim();
            Weights = new Dictionary<string, double>();

            for (int i = 0; i < labels.Count; i++)
                Weights[labels[i].Trim()] = multipliers[i];
        }

        // ── Accessors ─────────────────────────────────────────────────────────

        /// <summary>
        /// Get the multiplier for a channel label.
        /// Returns 0.0 if the label is not present (channel ignored).
        /// </summary>
        public double GetWeight(string label)
        {
            double value;
            return Weights.TryGetValue(label, out value) ? value : 0.0;
        }

        /// <summary>
        /// Returns true if this ValueSet has a non-zero weight for the label.
        /// </summary>
        public bool HasChannel(string label)
        {
            double value;
            return Weights.TryGetValue(label, out value) && value != 0.0;
        }

        /// <summary>
        /// Update or add a weight for a channel label.
        /// </summary>
        public void SetWeight(string label, double multiplier)
        {
            if (string.IsNullOrWhiteSpace(label))
                throw new ArgumentException("label must be a non-empty string.");
            Weights[label.Trim()] = multiplier;
        }

        // ── Summary ───────────────────────────────────────────────────────────

        /// <summary>
        /// Human-readable summary of all channel weights.
        /// </summary>
        public string Summary()
        {
            var lines = new System.Text.StringBuilder();
            lines.AppendLine(string.Format("ValueSet — program: '{0}'", ProgramName));
            lines.AppendLine("  Channel weights:");

            foreach (var kvp in Weights.OrderBy(k => k.Key))
            {
                string direction;
                if (kvp.Value == 0.0)
                    direction = "ignored";
                else if (kvp.Value > 0)
                    direction = string.Format("prefer HIGH  (m={0:+0.00;-0.00})", kvp.Value);
                else
                    direction = string.Format("prefer LOW   (m={0:+0.00;-0.00})", kvp.Value);

                lines.AppendLine(string.Format("    '{0}' -> {1}", kvp.Key, direction));
            }

            return lines.ToString().TrimEnd();
        }

        /// <summary>
        /// Returns a compact string representation.
        /// </summary>
        public override string ToString()
        {
            var rules = string.Join(", ",
                Weights.Select(kvp => string.Format("{0}:{1:+0.00;-0.00}", kvp.Key, kvp.Value)));
            return string.Format("ValueSet(program='{0}', weights=[{1}])", ProgramName, rules);
        }
    }
}

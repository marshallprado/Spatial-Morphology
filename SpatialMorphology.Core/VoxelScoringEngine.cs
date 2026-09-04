// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;

namespace SpatialMorphology
{
    /// <summary>
    /// Assignment strategy used by <see cref="VoxelScoringEngine"/>.
    /// Integer values match the legacy <c>method</c> input parameter and must not change.
    /// </summary>
    public enum AssignmentMethod
    {
        /// <summary>0 — every (voxel, program) score sorted descending globally; highest wins first.</summary>
        HighestScoreFirst = 0,

        /// <summary>1 — each program claims its best remaining voxel in turn.</summary>
        RoundRobin = 1,

        /// <summary>2 — program 0 fills completely, then program 1, and so on.</summary>
        PerProgram = 2
    }

    /// <summary>Minimal, Rhino-free description of a program for scoring purposes.</summary>
    /// <remarks>
    /// Deliberately does NOT carry a colour. <c>System.Drawing.Common</c> is
    /// Windows-only from .NET 6 onward, so colour stays in the Grasshopper layer.
    /// </remarks>
    public sealed class ProgramSpec
    {
        /// <summary>Program name. Must match a ValueSet's ProgramName to receive weights.</summary>
        public string Name { get; }

        /// <summary>Target voxel count, or -1 for unlimited. Only enforced when showAll is false.</summary>
        public int VoxelCount { get; }

        /// <summary>Creates a program specification.</summary>
        public ProgramSpec(string name, int voxelCount = -1)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("name must be a non-empty string.", nameof(name));

            Name = name.Trim();
            VoxelCount = voxelCount;
        }
    }

    /// <summary>Outcome of a scoring + assignment run.</summary>
    public sealed class ScoringResult
    {
        /// <summary>Channel labels in input order.</summary>
        public List<string> Labels { get; }

        /// <summary>Per-label values normalised to 0..1.</summary>
        public Dictionary<string, List<double>> Channels { get; }

        /// <summary>Per-label values after NaN/Infinity sanitisation, before normalisation.</summary>
        public Dictionary<string, List<double>> Raw { get; }

        /// <summary>
        /// Program index per voxel. <c>-1</c> = unassigned, <c>-2</c> = reserved for core.
        /// These sentinel values are part of the public contract.
        /// </summary>
        public List<int> ProgramIndices { get; }

        /// <summary>Score of the winning program for each voxel; 0.0 where unassigned.</summary>
        public List<double> WinningScore { get; }

        /// <summary>Voxel indices per program, ordered best score first.</summary>
        public List<List<int>> Ranked { get; }

        /// <summary>Number of voxels.</summary>
        public int VoxelCount { get; }

        internal ScoringResult(
            List<string> labels,
            Dictionary<string, List<double>> channels,
            Dictionary<string, List<double>> raw,
            List<int> programIndices,
            List<double> winningScore,
            List<List<int>> ranked,
            int voxelCount)
        {
            Labels = labels;
            Channels = channels;
            Raw = raw;
            ProgramIndices = programIndices;
            WinningScore = winningScore;
            Ranked = ranked;
            VoxelCount = voxelCount;
        }

        /// <summary>
        /// Display alpha for a voxel, 50..255 scaled across the global assigned-score
        /// range, or 40 for unassigned/core voxels.
        /// </summary>
        public int AlphaFor(int voxel)
        {
            if (ProgramIndices[voxel] < 0) return 40;

            var assignedScores = new List<double>();
            for (int v = 0; v < VoxelCount; v++)
                if (ProgramIndices[v] >= 0)
                    assignedScores.Add(WinningScore[v]);

            if (assignedScores.Count == 0) return 40;

            double lo = assignedScores.Min();
            double hi = assignedScores.Max();
            return AlphaFromRange(WinningScore[voxel], lo, hi);
        }

        /// <summary>
        /// Alpha values for every voxel. Preferred over repeated <see cref="AlphaFor"/>
        /// calls — computes the global range once instead of per voxel.
        /// </summary>
        public List<int> AllAlphas()
        {
            var assignedScores = new List<double>();
            for (int v = 0; v < VoxelCount; v++)
                if (ProgramIndices[v] >= 0)
                    assignedScores.Add(WinningScore[v]);

            double lo = assignedScores.Count > 0 ? assignedScores.Min() : 0.0;
            double hi = assignedScores.Count > 0 ? assignedScores.Max() : 0.0;

            var result = new List<int>(VoxelCount);
            for (int v = 0; v < VoxelCount; v++)
                result.Add(ProgramIndices[v] < 0
                    ? 40
                    : AlphaFromRange(WinningScore[v], lo, hi));

            return result;
        }

        private static int AlphaFromRange(double score, double lo, double hi)
        {
            double t = hi > lo ? (score - lo) / (hi - lo) : 0.0;
            return Math.Max(50, Math.Min(255, (int)Math.Round(50 + t * 205)));
        }
    }

    /// <summary>Raised when the caller supplies inconsistent scoring input.</summary>
    public class ScoringInputException : Exception
    {
        /// <summary>Creates the exception with a caller-facing message.</summary>
        public ScoringInputException(string message) : base(message) { }
    }

    /// <summary>
    /// Pure scoring and assignment logic extracted from AnalysisStackComponent.
    /// No Rhino, no Grasshopper, no System.Drawing — fully unit-testable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a faithful extraction, not a redesign. Normalisation, multiplier
    /// semantics, all three assignment methods, ranking order and the alpha ramp
    /// reproduce the original component exactly, so existing definitions produce
    /// identical results.
    /// </para>
    /// <para>
    /// <b>Missing-channel default.</b> A channel absent from a program's ValueSet is
    /// treated as multiplier <c>1.0</c> (counts fully, prefer high) — matching the
    /// original component. Note that <c>ValueSet.GetWeight</c> returns <c>0.0</c>
    /// (ignore) for the same case. The component never called that accessor, so the
    /// two rules never met. The component's behaviour is authoritative here because
    /// it is what saved .gh files already produce. Do not "fix" this by routing
    /// through GetWeight without a deliberate migration.
    /// </para>
    /// </remarks>
    public static class VoxelScoringEngine
    {
        /// <summary>
        /// Scores every voxel for every program and assigns each voxel to one program.
        /// </summary>
        /// <param name="voxelCount">Number of voxels. Every channel must have this many values.</param>
        /// <param name="analyses">Analysis channels. Labels must be unique.</param>
        /// <param name="programs">Programs, in output branch order.</param>
        /// <param name="valueSets">Optional per-program weights, matched by name. May be null.</param>
        /// <param name="showAll">
        /// True = ignore each program's VoxelCount cap.
        /// False = stop assigning to a program once its cap is reached.
        /// </param>
        /// <param name="method">Assignment strategy.</param>
        /// <param name="coreVoxels">
        /// Optional voxel indices reserved for the core. Marked <c>-2</c> and excluded
        /// from program assignment. May be null.
        /// </param>
        /// <exception cref="ScoringInputException">
        /// Duplicate channel label, channel length mismatch, or no channels/programs.
        /// </exception>
        public static ScoringResult Run(
            int voxelCount,
            IList<SpatialAnalysis> analyses,
            IList<ProgramSpec> programs,
            IList<ValueSet>? valueSets,
            bool showAll,
            AssignmentMethod method,
            IEnumerable<int>? coreVoxels = null)
        {
            if (analyses == null) throw new ArgumentNullException(nameof(analyses));
            if (programs == null) throw new ArgumentNullException(nameof(programs));

            if (analyses.Count == 0)
                throw new ScoringInputException("No SpatialAnalysis channels supplied.");
            if (programs.Count == 0)
                throw new ScoringInputException("No programs supplied.");

            int n = voxelCount;

            // ── Step 1: validate + sanitise + normalise channels ─────────────────
            var labels = new List<string>();
            var channels = new Dictionary<string, List<double>>();
            var raw = new Dictionary<string, List<double>>();
            var seen = new HashSet<string>();

            foreach (var sa in analyses)
            {
                if (sa.Values.Count != n)
                    throw new ScoringInputException(string.Format(
                        "Channel '{0}' has {1} values but voxel_grid has {2} voxels.",
                        sa.Label, sa.Values.Count, n));

                if (seen.Contains(sa.Label))
                    throw new ScoringInputException(string.Format(
                        "Duplicate channel label '{0}'.", sa.Label));

                var sanitized = new List<double>(n);
                foreach (var v in sa.Values)
                    sanitized.Add(IsUnusable(v) ? 0.0 : v);

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

            // ── Step 2: multiplier lookup, keyed by program name ─────────────────
            var valueSetMap = new Dictionary<string, Dictionary<string, double>>();
            if (valueSets != null)
                foreach (var vs in valueSets)
                    valueSetMap[vs.ProgramName] = vs.Weights;

            // ── Step 3: score every voxel for every program ──────────────────────
            // m > 0 rewards high normalised values; m < 0 rewards low ones by
            // scoring against (1 - value). m == 0 skips the channel entirely.
            int nPrograms = programs.Count;
            var rawScores = new List<List<double>>(nPrograms);

            for (int p = 0; p < nPrograms; p++)
            {
                var weights = valueSetMap.ContainsKey(programs[p].Name)
                    ? valueSetMap[programs[p].Name]
                    : null;

                var s = new List<double>(new double[n]);

                foreach (var lbl in labels)
                {
                    var ch = channels[lbl];

                    // Absent channel defaults to 1.0 — see remarks on the class.
                    double m = (weights != null && weights.ContainsKey(lbl))
                        ? weights[lbl]
                        : 1.0;

                    if (m == 0.0) continue;

                    if (m > 0)
                    {
                        for (int i = 0; i < n; i++)
                            s[i] += m * ch[i];
                    }
                    else
                    {
                        double absM = Math.Abs(m);
                        for (int i = 0; i < n; i++)
                            s[i] += absM * (1.0 - ch[i]);
                    }
                }

                rawScores.Add(s);
            }

            // ── Step 4: assignment ───────────────────────────────────────────────
            var programIndices = new List<int>(new int[n]);
            var winningScore = new List<double>(new double[n]);
            var assigned = new HashSet<int>();

            for (int i = 0; i < n; i++)
                programIndices[i] = -1;

            var coreSet = new HashSet<int>();
            if (coreVoxels != null)
                foreach (var idx in coreVoxels)
                    if (idx >= 0 && idx < n)
                        coreSet.Add(idx);

            foreach (var idx in coreSet)
                programIndices[idx] = -2;

            var eligible = Enumerable.Range(0, n)
                .Where(i => programIndices[i] != -2)
                .ToList();

            switch (method)
            {
                case AssignmentMethod.HighestScoreFirst:
                    AssignHighestScoreFirst(
                        eligible, nPrograms, programs, rawScores,
                        showAll, programIndices, winningScore, assigned);
                    break;

                case AssignmentMethod.RoundRobin:
                    AssignRoundRobin(
                        eligible, nPrograms, programs, rawScores,
                        showAll, programIndices, winningScore, assigned);
                    break;

                default:
                    AssignPerProgram(
                        eligible, nPrograms, programs, rawScores,
                        showAll, programIndices, winningScore, assigned);
                    break;
            }

            // ── Step 5: rank each program's voxels best → worst ──────────────────
            var ranked = new List<List<int>>(nPrograms);
            for (int p = 0; p < nPrograms; p++)
            {
                int program = p;
                ranked.Add(Enumerable.Range(0, n)
                    .Where(v => programIndices[v] == program)
                    .OrderByDescending(v => winningScore[v])
                    .ToList());
            }

            return new ScoringResult(
                labels, channels, raw,
                programIndices, winningScore, ranked, n);
        }

        /// <summary>
        /// Matches the original sanitisation: NaN and +Infinity become 0.0.
        /// </summary>
        /// <remarks>
        /// NegativeInfinity is deliberately NOT filtered, reproducing the original
        /// behaviour. It would propagate into normalisation and produce NaN scores.
        /// Left as-is so this extraction changes no results; fix it as its own
        /// commit with a test, not silently here.
        /// </remarks>
        private static bool IsUnusable(double v)
            => double.IsNaN(v) || v == double.PositiveInfinity;

        // ── Method 0 ─────────────────────────────────────────────────────────────
        // All (voxel, program) pairs sorted by score descending. First claim wins.
        // Ties resolve by the sort's ordering, as before.
        private static void AssignHighestScoreFirst(
            List<int> eligible,
            int nPrograms,
            IList<ProgramSpec> programs,
            List<List<double>> rawScores,
            bool showAll,
            List<int> programIndices,
            List<double> winningScore,
            HashSet<int> assigned)
        {
            var allScores = new List<(double score, int voxel, int program)>();
            foreach (var v in eligible)
                for (int p = 0; p < nPrograms; p++)
                    allScores.Add((rawScores[p][v], v, p));

            allScores.Sort((a, b) => b.score.CompareTo(a.score));

            var counts = new int[nPrograms];

            foreach (var (score, v, p) in allScores)
            {
                if (assigned.Contains(v)) continue;

                if (!showAll && programs[p].VoxelCount >= 0 &&
                    counts[p] >= programs[p].VoxelCount)
                    continue;

                programIndices[v] = p;
                winningScore[v] = score;
                assigned.Add(v);
                counts[p]++;
            }
        }

        // ── Method 1 ─────────────────────────────────────────────────────────────
        // Each program holds a queue of voxels sorted by its own score. One pass
        // per program per round; a program claims at most one voxel per round.
        private static void AssignRoundRobin(
            List<int> eligible,
            int nPrograms,
            IList<ProgramSpec> programs,
            List<List<double>> rawScores,
            bool showAll,
            List<int> programIndices,
            List<double> winningScore,
            HashSet<int> assigned)
        {
            var counts = new int[nPrograms];
            var queues = new List<Queue<(double score, int voxel)>>(nPrograms);

            for (int p = 0; p < nPrograms; p++)
            {
                int program = p;
                var sorted = eligible
                    .Select(v => (rawScores[program][v], v))
                    .OrderByDescending(x => x.Item1)
                    .ToList();
                queues.Add(new Queue<(double, int)>(sorted));
            }

            bool anyActive = true;
            while (anyActive)
            {
                anyActive = false;

                for (int p = 0; p < nPrograms; p++)
                {
                    if (!showAll && programs[p].VoxelCount >= 0 &&
                        counts[p] >= programs[p].VoxelCount)
                        continue;

                    while (queues[p].Count > 0)
                    {
                        var (score, v) = queues[p].Dequeue();
                        if (assigned.Contains(v)) continue;

                        programIndices[v] = p;
                        winningScore[v] = score;
                        assigned.Add(v);
                        counts[p]++;
                        anyActive = true;
                        break;
                    }
                }
            }
        }

        // ── Method 2 ─────────────────────────────────────────────────────────────
        // Program 0 takes everything it wants, then program 1, and so on.
        private static void AssignPerProgram(
            List<int> eligible,
            int nPrograms,
            IList<ProgramSpec> programs,
            List<List<double>> rawScores,
            bool showAll,
            List<int> programIndices,
            List<double> winningScore,
            HashSet<int> assigned)
        {
            var counts = new int[nPrograms];

            for (int p = 0; p < nPrograms; p++)
            {
                int program = p;
                var sorted = eligible
                    .Where(v => !assigned.Contains(v))
                    .OrderByDescending(v => rawScores[program][v])
                    .ToList();

                foreach (var v in sorted)
                {
                    if (!showAll && programs[program].VoxelCount >= 0 &&
                        counts[program] >= programs[program].VoxelCount)
                        break;

                    programIndices[v] = program;
                    winningScore[v] = rawScores[program][v];
                    assigned.Add(v);
                    counts[program]++;
                }
            }
        }
    }
}

"""
core/analysis_stack.py
======================
AnalysisStack class — voxel assignment solver.

Collects SpatialAnalysis channels, scores every voxel for every program,
and assigns each voxel to its best-matching program.
"""

import System.Drawing as sd


class AnalysisStack(object):
    """
    Voxel assignment solver.

    Attributes
    ----------
    voxel_grid      : VoxelGrid
    n_voxels        : int
    labels          : list[str]
    channels        : dict{label: list[float]}   normalized [0,1]
    raw             : dict{label: list[float]}   pre-normalization
    programs        : list[ProgramDefinition]
    program_indices : list[int]                  per-voxel assignment (-1=unassigned)
    scores          : list[list[float]]          scores[p][v]
    ranked          : list[list[int]]            ranked[p] = voxel indices best→worst
    """

    def __init__(self, voxel_grid, spatial_analyses, programs,
                 value_sets=None, show_all=True):
        n = len(voxel_grid.filled_keys)

        self.voxel_grid = voxel_grid
        self.n_voxels   = n
        self.programs   = list(programs)
        self.labels     = []
        self.channels   = {}
        self.raw        = {}

        # ── Step 1: collect and normalize SA channels ─────────────────────────
        seen = set()
        for sa in spatial_analyses:
            if not hasattr(sa, "label") or not hasattr(sa, "values"):
                raise TypeError(
                    "Expected SpatialAnalysis, got {}.".format(type(sa).__name__))
            lbl = sa.label
            if lbl in seen:
                raise ValueError(
                    "Duplicate channel label '{}'. Each SA component must "
                    "have a unique label.".format(lbl))
            if len(sa.values) != n:
                raise ValueError(
                    "Channel '{}' has {} values but voxel_grid has {} "
                    "voxels.".format(lbl, len(sa.values), n))

            sanitized = []
            for v in sa.values:
                if v is None:
                    sanitized.append(0.0)
                else:
                    f = float(v)
                    sanitized.append(0.0 if f != f else f)

            lo, hi = min(sanitized), max(sanitized)
            normalized = [(v - lo) / (hi - lo) for v in sanitized] \
                         if hi > lo else [0.0] * n

            seen.add(lbl)
            self.labels.append(lbl)
            self.raw[lbl]      = sanitized
            self.channels[lbl] = normalized

        # ── Step 2: build multiplier table from ValueSets ─────────────────────
        value_set_map = {}
        if value_sets:
            for vs in value_sets:
                if hasattr(vs, "program_name") and hasattr(vs, "weights"):
                    value_set_map[vs.program_name] = vs.weights

        # ── Step 3: score every voxel for every program ───────────────────────
        n_programs = len(self.programs)
        raw_scores = []

        for prog in self.programs:
            weights = value_set_map.get(prog.name, None)
            s = [0.0] * n
            for lbl in self.labels:
                ch = self.channels[lbl]
                m  = weights[lbl] if (weights and lbl in weights) else 1.0
                if m == 0.0:
                    continue
                if m > 0:
                    for i in range(n):
                        s[i] += m * ch[i]
                else:
                    abs_m = abs(m)
                    for i in range(n):
                        s[i] += abs_m * (1.0 - ch[i])
            raw_scores.append(s)

        # ── Step 4: assign each voxel to its highest-scoring program ──────────
        program_indices = [-1] * n
        winning_score   = [0.0] * n

        for v in range(n):
            best_p  = -1
            best_sc = -1.0
            for p_idx in range(n_programs):
                sc = raw_scores[p_idx][v]
                if sc > best_sc:
                    best_sc = sc
                    best_p  = p_idx
            program_indices[v] = best_p
            winning_score[v]   = best_sc if best_p >= 0 else 0.0

        # ── Step 5: rank voxels per program best→worst ────────────────────────
        ranked = []
        for p_idx in range(n_programs):
            assigned = [v for v in range(n) if program_indices[v] == p_idx]
            assigned.sort(key=lambda v: winning_score[v], reverse=True)
            ranked.append(assigned)

        # ── Step 6: clamp to voxel_count if show_all=False ───────────────────
        if not show_all:
            for p_idx, prog in enumerate(self.programs):
                if prog.voxel_count >= 0:
                    clamped = ranked[p_idx][:prog.voxel_count]
                    removed = set(ranked[p_idx][prog.voxel_count:])
                    for v in removed:
                        program_indices[v] = -1
                    ranked[p_idx] = clamped

        self.program_indices = program_indices
        self.winning_score   = winning_score
        self.ranked          = ranked

        # ── Step 7: global alpha mapping ─────────────────────────────────────
        # Alpha reflects winning score mapped globally across ALL voxels.
        # Best voxel globally → alpha 255, worst → alpha 50.
        assigned_scores = [
            winning_score[v] for v in range(n)
            if program_indices[v] >= 0
        ]
        if assigned_scores:
            g_lo = min(assigned_scores)
            g_hi = max(assigned_scores)
        else:
            g_lo = g_hi = 0.0

        def alpha_for(v):
            if program_indices[v] < 0:
                return 40
            sc = winning_score[v]
            if g_hi > g_lo:
                t = (sc - g_lo) / (g_hi - g_lo)
            else:
                t = 0.0
            return int(round(50 + t * (255 - 50)))

        self._alpha_for = alpha_for

    # ── Shader helper ─────────────────────────────────────────────────────────

    def shader_for(self, voxel_idx):
        """
        Return System.Drawing.Color for voxel_idx.
        Hue = assigned program color. Alpha = global performance mapping.
        Unassigned voxels → grey A=40.
        """
        p_idx = self.program_indices[voxel_idx]
        a     = self._alpha_for(voxel_idx)
        if p_idx < 0:
            return sd.Color.FromArgb(40, 160, 160, 160)
        c = self.programs[p_idx].color
        return sd.Color.FromArgb(a, c.R, c.G, c.B)

    # ── Accessors ─────────────────────────────────────────────────────────────

    def get(self, label):
        if label not in self.channels:
            raise ValueError(
                "Channel '{}' not found. Available: {}".format(
                    label, self.labels))
        return self.channels[label]

    def get_raw(self, label):
        if label not in self.raw:
            raise ValueError(
                "Channel '{}' not found. Available: {}".format(
                    label, self.labels))
        return self.raw[label]

    def voxel_vector(self, voxel_idx):
        return {lbl: self.channels[lbl][voxel_idx] for lbl in self.labels}

    def __repr__(self):
        return "AnalysisStack(voxels={}, channels=[{}], programs=[{}])".format(
            self.n_voxels,
            ", ".join(self.labels),
            ", ".join(p.name for p in self.programs))

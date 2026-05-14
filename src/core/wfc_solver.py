
"""
core/wfc_solver.py
==================
WFCSolver class — deterministic performance-guided Wave Function Collapse.

Previously defined inside the GH WFC component.
"""

import copy
from collections import deque


OPPOSITE = {"px":"nx","nx":"px","py":"ny","ny":"py","pz":"nz","nz":"pz"}
DIRS = [
    ("px", 1,0,0), ("nx",-1,0,0),
    ("py", 0,1,0), ("ny", 0,-1,0),
    ("pz", 0,0,1), ("nz", 0,0,-1),
]


def compatible(tile_a, direction, tile_b):
    opp = OPPOSITE[direction]
    return (
        tile_b.tile_type in tile_a.neighbors[direction] and
        tile_a.tile_type in tile_b.neighbors[opp]
    )


def build_compat_table(tiles):
    table = {}
    for i, ta in enumerate(tiles):
        for d, _,_,_ in DIRS:
            table[(i, d)] = frozenset(
                j for j, tb in enumerate(tiles)
                if compatible(ta, d, tb))
    return table


class WFCSolver(object):
    """
    Deterministic performance-guided WFC solver.

    Uses per-tile ranking queues built from the voxel_ranking DataTree.
    No randomness — collapse order is fully determined by performance scores.
    """

    def __init__(self, filled_keys, tiles, compat_table, tile_ranking_lists):
        self.filled_keys     = filled_keys
        self.key_to_vidx     = {k: i for i, k in enumerate(filled_keys)}
        self.vidx_to_key     = {i: k for i, k in enumerate(filled_keys)}
        self.tiles           = tiles
        self.n_tiles         = len(tiles)
        self.compat          = compat_table
        self.counts          = [0] * len(tiles)
        self.collapsed_count = 0

        # ── Phase 1 fix: exclude max_count=0 tiles from initial wave ──────────
        # A tile with max_count=0 can never be placed. Removing it from the
        # initial superposition prevents it from being collapsed before
        # _remove_exhausted has a chance to run.
        valid_ids = frozenset(
            tid for tid, tile in enumerate(tiles)
            if tile.max_count != 0
        )
        self.wave = {k: set(valid_ids) for k in filled_keys}

        self._tile_queues = [
            deque(tile_ranking_lists[t])
            for t in range(self.n_tiles)
        ]

    # ── AC-3 propagation ──────────────────────────────────────────────────────

    def _propagate(self, start_key):
        queue    = deque([start_key])
        in_queue = {start_key}
        while queue:
            key = queue.popleft()
            in_queue.discard(key)
            ix, iy, iz = key
            for d, dx, dy, dz in DIRS:
                nb = (ix+dx, iy+dy, iz+dz)
                if nb not in self.wave:
                    continue
                allowed = set()
                for tid in self.wave[key]:
                    allowed |= self.compat[(tid, d)]
                new_nb = self.wave[nb] & allowed
                if not new_nb:
                    return False
                if new_nb != self.wave[nb]:
                    self.wave[nb] = new_nb
                    # Guard: len(opts) <= 1 check in _step prevents
                    # double-counting when queue later reaches this voxel.
                    if len(new_nb) == 1:
                        tid = next(iter(new_nb))
                        self.counts[tid] += 1
                        self.collapsed_count += 1
                    if nb not in in_queue:
                        queue.append(nb)
                        in_queue.add(nb)
        return True

    # ── Max-count enforcement ─────────────────────────────────────────────────

    def _remove_exhausted(self):
        exhausted = {
            tid for tid, tile in enumerate(self.tiles)
            if tile.max_count >= 0 and self.counts[tid] >= tile.max_count
        }
        if not exhausted:
            return True
        changed = []
        for key, opts in self.wave.items():
            if len(opts) <= 1:
                continue
            new_opts = opts - exhausted
            if not new_opts:
                return False
            if new_opts != opts:
                self.wave[key] = new_opts
                changed.append(key)
        for key in changed:
            if not self._propagate(key):
                return False
        return True

    # ── Performance-guided collapse step ──────────────────────────────────────

    def _step(self):
        """
        Iterate tile queues in tile-index order. For each tile, advance
        its queue to the next voxel that is uncollapsed AND still has that
        tile as a valid option. Collapse it.

        Returns collapsed key or None if all queues exhausted.
        """
        for t_idx, queue in enumerate(self._tile_queues):
            while queue:
                vidx = queue[0]
                key  = self.vidx_to_key.get(vidx)
                if key is None:
                    queue.popleft()
                    continue
                opts = self.wave.get(key, set())
                # len(opts) <= 1: already collapsed by propagation — skip.
                # Critical guard — prevents double-counting with _propagate.
                if len(opts) <= 1:
                    queue.popleft()
                    continue
                if t_idx not in opts:
                    queue.popleft()
                    continue
                queue.popleft()
                self.wave[key] = {t_idx}
                self.counts[t_idx] += 1
                self.collapsed_count += 1
                return key

        return None

    # ── Main solve loop ───────────────────────────────────────────────────────

    def solve(self, target_count):
        """
        Collapse until target_count reached, queues exhausted, or
        contradiction found.

        Returns True if no contradiction occurred.
        """
        max_iter = len(self.filled_keys) * 4

        for _ in range(max_iter):
            if self.collapsed_count >= target_count:
                return True

            key = self._step()
            if key is None:
                return True

            if not self._propagate(key):
                return False

            if not self._remove_exhausted():
                return False

        return True

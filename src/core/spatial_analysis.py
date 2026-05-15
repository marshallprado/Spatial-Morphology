# -*- coding: utf-8 -*-
"""
core/spatial_analysis.py
========================
...
"""


class SpatialAnalysis(object):
    """
    Lightweight container pairing a channel label with its per-voxel values.

    Attributes
    ----------
    label  : str   Channel name, e.g. "adjacency", "depth", "proximity".
    values : list  Per-voxel raw values, parallel to voxel_grid.filled_keys.
                   Normalization is performed by AnalysisStack.
    """

    def __init__(self, label, values):
        self.label  = str(label).strip()
        self.values = list(values)

    def __repr__(self):
        n  = len(self.values)
        lo = min(self.values) if n else 0
        hi = max(self.values) if n else 0
        return "SpatialAnalysis(label={!r}, n={}, min={}, max={})".format(
            self.label, n, lo, hi)

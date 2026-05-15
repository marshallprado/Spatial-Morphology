"""
tests/test_spatial_analysis.py
================================
Unit tests for core/spatial_analysis.py
No Rhino dependency — pure Python.
"""

import sys
import os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'src'))

from core.spatial_analysis import SpatialAnalysis


# ── Construction ──────────────────────────────────────────────────────────────

def test_basic_construction():
    sa = SpatialAnalysis("depth", [0, 1, 2, 3])
    assert sa.label == "depth"
    assert sa.values == [0, 1, 2, 3]

def test_label_stripped():
    sa = SpatialAnalysis("  depth  ", [1, 2, 3])
    assert sa.label == "depth"

def test_label_converted_to_str():
    sa = SpatialAnalysis(42, [1, 2, 3])
    assert sa.label == "42"

def test_values_converted_to_list():
    sa = SpatialAnalysis("depth", (1, 2, 3))
    assert isinstance(sa.values, list)
    assert sa.values == [1, 2, 3]

def test_empty_values():
    sa = SpatialAnalysis("depth", [])
    assert sa.values == []

def test_single_value():
    sa = SpatialAnalysis("depth", [0.5])
    assert sa.values == [0.5]


# ── Repr ──────────────────────────────────────────────────────────────────────

def test_repr_contains_label():
    sa = SpatialAnalysis("adjacency", [0, 3, 6])
    assert "adjacency" in repr(sa)

def test_repr_contains_n():
    sa = SpatialAnalysis("depth", [1, 2, 3])
    assert "n=3" in repr(sa)

def test_repr_empty():
    sa = SpatialAnalysis("depth", [])
    assert repr(sa) is not None

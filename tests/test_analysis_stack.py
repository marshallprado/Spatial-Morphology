"""
tests/test_analysis_stack.py
==============================
Unit tests for core/analysis_stack.py
No Rhino dependency — VoxelGrid and ProgramDefinition are mocked.
"""

import sys
import os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'src'))

import pytest
from unittest.mock import MagicMock, patch
import unittest.mock as mock

from core.spatial_analysis import SpatialAnalysis

# ── Mocks ─────────────────────────────────────────────────────────────────────

def make_mock_voxel_grid(n=4):
    """Minimal VoxelGrid mock with n filled voxels."""
    vg = MagicMock()
    vg.filled_keys = [(i, 0, 0) for i in range(n)]
    return vg

def make_mock_program(name, r=255, g=0, b=0, voxel_count=-1):
    """Minimal ProgramDefinition mock."""
    p = MagicMock()
    p.name = name
    p.color = MagicMock()
    p.color.R = r
    p.color.G = g
    p.color.B = b
    p.voxel_count = voxel_count
    return p

def make_mock_sd():
    """Mock System.Drawing."""
    mock_color = MagicMock()
    mock_color.R = 255
    mock_color.G = 0
    mock_color.B = 0
    sd = MagicMock()
    sd.Color.FromArgb = MagicMock(return_value=mock_color)
    return sd

def import_analysis_stack():
    mock_sd = make_mock_sd()
    with mock.patch.dict('sys.modules', {
        'System': MagicMock(),
        'System.Drawing': mock_sd
    }):
        from core.analysis_stack import AnalysisStack
        return AnalysisStack

AnalysisStack = import_analysis_stack()


# ── Normalization ─────────────────────────────────────────────────────────────

def test_normalization_min_max():
    """Values should be normalized to [0, 1] via min-max."""
    vg = make_mock_voxel_grid(4)
    sa = SpatialAnalysis("depth", [0, 1, 2, 3])
    p  = make_mock_program("Office")

    stack = AnalysisStack(vg, [sa], [p])
    ch = stack.channels["depth"]

    assert abs(min(ch) - 0.0) < 1e-9
    assert abs(max(ch) - 1.0) < 1e-9

def test_normalization_flat_input():
    """All-equal values should normalize to all 0.0."""
    vg = make_mock_voxel_grid(4)
    sa = SpatialAnalysis("depth", [5, 5, 5, 5])
    p  = make_mock_program("Office")

    stack = AnalysisStack(vg, [sa], [p])
    ch = stack.channels["depth"]

    assert all(v == 0.0 for v in ch)

def test_normalization_preserves_order():
    """Relative order of values must be preserved after normalization."""
    vg = make_mock_voxel_grid(4)
    sa = SpatialAnalysis("depth", [10, 30, 20, 40])
    p  = make_mock_program("Office")

    stack = AnalysisStack(vg, [sa], [p])
    ch = stack.channels["depth"]

    assert ch[0] < ch[2] < ch[1] < ch[3]

def test_raw_values_stored():
    """Raw pre-normalization values should be accessible via get_raw()."""
    vg = make_mock_voxel_grid(4)
    sa = SpatialAnalysis("depth", [0, 10, 20, 30])
    p  = make_mock_program("Office")

    stack = AnalysisStack(vg, [sa], [p])
    raw = stack.get_raw("depth")

    assert raw == [0.0, 10.0, 20.0, 30.0]

def test_none_values_sanitized():
    """None values in sa.values should be replaced with 0.0."""
    vg = make_mock_voxel_grid(4)
    sa = SpatialAnalysis("depth", [None, 1, 2, 3])
    p  = make_mock_program("Office")

    stack = AnalysisStack(vg, [sa], [p])
    assert stack.get_raw("depth")[0] == 0.0

def test_nan_values_sanitized():
    """NaN values should be replaced with 0.0."""
    vg = make_mock_voxel_grid(4)
    sa = SpatialAnalysis("depth", [float('nan'), 1, 2, 3])
    p  = make_mock_program("Office")

    stack = AnalysisStack(vg, [sa], [p])
    assert stack.get_raw("depth")[0] == 0.0


# ── Channel validation ────────────────────────────────────────────────────────

def test_duplicate_label_raises():
    """Two SA objects with the same label should raise ValueError."""
    vg  = make_mock_voxel_grid(4)
    sa1 = SpatialAnalysis("depth", [0, 1, 2, 3])
    sa2 = SpatialAnalysis("depth", [1, 2, 3, 4])
    p   = make_mock_program("Office")

    with pytest.raises(ValueError, match="Duplicate"):
        AnalysisStack(vg, [sa1, sa2], [p])

def test_wrong_length_raises():
    """SA values length must match voxel count."""
    vg = make_mock_voxel_grid(4)
    sa = SpatialAnalysis("depth", [0, 1, 2])   # 3 values, grid has 4
    p  = make_mock_program("Office")

    with pytest.raises(ValueError, match="4"):
        AnalysisStack(vg, [sa], [p])

def test_multiple_channels():
    """Multiple SA channels should all be stored."""
    vg  = make_mock_voxel_grid(4)
    sa1 = SpatialAnalysis("depth",     [0, 1, 2, 3])
    sa2 = SpatialAnalysis("adjacency", [6, 4, 2, 0])
    p   = make_mock_program("Office")

    stack = AnalysisStack(vg, [sa1, sa2], [p])
    assert "depth"     in stack.channels
    assert "adjacency" in stack.channels
    assert stack.labels == ["depth", "adjacency"]


# ── Program assignment ────────────────────────────────────────────────────────

def test_all_voxels_assigned():
    """Every voxel should be assigned to a program."""
    vg = make_mock_voxel_grid(4)
    sa = SpatialAnalysis("depth", [0, 1, 2, 3])
    p  = make_mock_program("Office")

    stack = AnalysisStack(vg, [sa], [p])
    assert all(idx >= 0 for idx in stack.program_indices)

def test_single_program_gets_all_voxels():
    """With one program, all voxels should be assigned to it."""
    vg = make_mock_voxel_grid(4)
    sa = SpatialAnalysis("depth", [0, 1, 2, 3])
    p  = make_mock_program("Office")

    stack = AnalysisStack(vg, [sa], [p])
    assert all(idx == 0 for idx in stack.program_indices)

def test_two_programs_split_voxels():
    """
    With two programs and opposite multipliers, voxels should
    split between them — not all go to one program.
    """
    vg  = make_mock_voxel_grid(4)
    sa  = SpatialAnalysis("depth", [0, 1, 2, 3])
    p0  = make_mock_program("Office")       # prefers high depth
    p1  = make_mock_program("Lobby")        # prefers low depth

    # Mock value sets: p0 prefers high, p1 prefers low
    vs0 = MagicMock()
    vs0.program_name = "Office"
    vs0.weights = {"depth": 1.0}

    vs1 = MagicMock()
    vs1.program_name = "Lobby"
    vs1.weights = {"depth": -1.0}

    stack = AnalysisStack(vg, [sa], [p0, p1], value_sets=[vs0, vs1])

    assigned_to_0 = [i for i, p in enumerate(stack.program_indices) if p == 0]
    assigned_to_1 = [i for i, p in enumerate(stack.program_indices) if p == 1]

    assert len(assigned_to_0) > 0
    assert len(assigned_to_1) > 0

def test_ranked_order_best_to_worst():
    """Ranked voxels for a program should be ordered best→worst."""
    vg = make_mock_voxel_grid(4)
    sa = SpatialAnalysis("depth", [0, 1, 2, 3])
    p  = make_mock_program("Office")

    stack  = AnalysisStack(vg, [sa], [p])
    ranked = stack.ranked[0]

    scores = [stack.winning_score[v] for v in ranked]
    assert scores == sorted(scores, reverse=True)


# ── show_all / voxel_count clamping ──────────────────────────────────────────

def test_show_all_true_ignores_voxel_count():
    """show_all=True should return all assigned voxels regardless of voxel_count."""
    vg = make_mock_voxel_grid(4)
    sa = SpatialAnalysis("depth", [0, 1, 2, 3])
    p  = make_mock_program("Office", voxel_count=1)

    stack = AnalysisStack(vg, [sa], [p], show_all=True)
    assert len(stack.ranked[0]) > 1

def test_show_all_false_clamps_to_voxel_count():
    """show_all=False should clamp each program to its voxel_count."""
    vg = make_mock_voxel_grid(4)
    sa = SpatialAnalysis("depth", [0, 1, 2, 3])
    p  = make_mock_program("Office", voxel_count=2)

    stack = AnalysisStack(vg, [sa], [p], show_all=False)
    assert len(stack.ranked[0]) <= 2

def test_unlimited_voxel_count_not_clamped():
    """voxel_count=-1 should never be clamped."""
    vg = make_mock_voxel_grid(4)
    sa = SpatialAnalysis("depth", [0, 1, 2, 3])
    p  = make_mock_program("Office", voxel_count=-1)

    stack = AnalysisStack(vg, [sa], [p], show_all=False)
    assert len(stack.ranked[0]) == 4


# ── Accessors ─────────────────────────────────────────────────────────────────

def test_get_channel():
    vg = make_mock_voxel_grid(4)
    sa = SpatialAnalysis("depth", [0, 1, 2, 3])
    p  = make_mock_program("Office")

    stack = AnalysisStack(vg, [sa], [p])
    ch = stack.get("depth")
    assert len(ch) == 4

def test_get_missing_channel_raises():
    vg = make_mock_voxel_grid(4)
    sa = SpatialAnalysis("depth", [0, 1, 2, 3])
    p  = make_mock_program("Office")

    stack = AnalysisStack(vg, [sa], [p])
    with pytest.raises(ValueError):
        stack.get("nonexistent")

def test_voxel_vector():
    vg  = make_mock_voxel_grid(4)
    sa1 = SpatialAnalysis("depth",     [0, 1, 2, 3])
    sa2 = SpatialAnalysis("adjacency", [6, 4, 2, 0])
    p   = make_mock_program("Office")

    stack = AnalysisStack(vg, [sa1, sa2], [p])
    vec = stack.voxel_vector(0)

    assert "depth"     in vec
    assert "adjacency" in vec

def test_repr():
    vg = make_mock_voxel_grid(4)
    sa = SpatialAnalysis("depth", [0, 1, 2, 3])
    p  = make_mock_program("Office")

    stack = AnalysisStack(vg, [sa], [p])
    assert "AnalysisStack" in repr(stack)
    assert "depth" in repr(stack)

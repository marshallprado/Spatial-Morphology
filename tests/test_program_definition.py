"""
tests/test_program_definition.py
==================================
Unit tests for core/program_definition.py
No Rhino dependency — pure Python.
"""

import sys
import os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'src'))

import pytest

# System.Drawing is a .NET library only available inside Rhino.
# We mock it here so the tests can run in plain Python / pytest.
from unittest.mock import MagicMock
import unittest.mock as mock

# Build a minimal System.Drawing.Color mock
mock_color = MagicMock()
mock_color.R = 255
mock_color.G = 0
mock_color.B = 0

mock_sd = MagicMock()
mock_sd.Color.FromArgb.return_value = mock_color
mock_sd.Color = MagicMock()
mock_sd.Color.FromArgb = MagicMock(return_value=mock_color)

# Patch System.Drawing before importing ProgramDefinition
with mock.patch.dict('sys.modules', {'System': MagicMock(), 'System.Drawing': mock_sd}):
    from core.program_definition import ProgramDefinition


# ── Construction ──────────────────────────────────────────────────────────────

def make_program(name="Office", color=None, voxel_count=-1):
    """Helper — creates a ProgramDefinition with mocked color."""
    with mock.patch.dict('sys.modules', {'System': MagicMock(), 'System.Drawing': mock_sd}):
        return ProgramDefinition(name=name, color=color, voxel_count=voxel_count)

def test_basic_construction():
    p = make_program("Office")
    assert p.name == "Office"
    assert p.voxel_count == -1

def test_name_stripped():
    p = make_program("  Office  ")
    assert p.name == "Office"

def test_voxel_count_stored():
    p = make_program("Office", voxel_count=50)
    assert p.voxel_count == 50

def test_voxel_count_unlimited():
    p = make_program("Office", voxel_count=-1)
    assert p.voxel_count == -1

def test_voxel_count_negative_other_than_minus_one():
    p = make_program("Office", voxel_count=-5)
    assert p.voxel_count == -5


# ── Validation ────────────────────────────────────────────────────────────────

def test_empty_name_raises():
    with pytest.raises(ValueError):
        make_program("")

def test_whitespace_name_raises():
    with pytest.raises(ValueError):
        make_program("   ")

def test_voxel_count_zero_raises():
    with pytest.raises(ValueError):
        make_program("Office", voxel_count=0)

def test_none_name_raises():
    with pytest.raises((ValueError, AttributeError)):
        make_program(None)


# ── Repr / summary ────────────────────────────────────────────────────────────

def test_repr_contains_name():
    p = make_program("Office")
    assert "Office" in repr(p)

def test_summary_contains_name():
    p = make_program("Office", voxel_count=10)
    assert "Office" in p.summary()

def test_summary_contains_voxel_count():
    p = make_program("Office", voxel_count=10)
    assert "10" in p.summary()

def test_summary_unlimited():
    p = make_program("Office", voxel_count=-1)
    assert "unlimited" in p.summary()

# -*- coding: utf-8 -*-
"""
tests/test_voxel_grid.py
========================
Unit tests for core/voxel_grid.py
No Rhino dependency — Rhino geometry is mocked.
"""

import sys
import os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'src'))

import pytest
import math
from unittest.mock import MagicMock, patch


# ── Mock Rhino.Geometry ───────────────────────────────────────────────────────

class MockPoint3d:
    def __init__(self, x, y, z):
        self.X = x
        self.Y = y
        self.Z = z
    def DistanceTo(self, other):
        return math.sqrt(
            (self.X - other.X)**2 +
            (self.Y - other.Y)**2 +
            (self.Z - other.Z)**2)

class MockBoundingBox:
    def __init__(self, min_pt, max_pt):
        self.Min = min_pt
        self.Max = max_pt

class MockMesh:
    def __init__(self, bbox):
        self._bbox = bbox
    def GetBoundingBox(self, accurate):
        return self._bbox
    def ClosestPoint(self, pt):
        return MockPoint3d(0, 0, 0)
    def IsPointInside(self, pt, tolerance, strictly):
        return True

mock_rg = MagicMock()
mock_rg.Point3d.side_effect = lambda x, y, z: MockPoint3d(x, y, z)
mock_rg.Interval.side_effect = lambda a, b: MagicMock()
mock_rg.Plane.side_effect = lambda *a: MagicMock()
mock_rg.Box.side_effect = lambda *a: MagicMock()
mock_rg.Vector3d.ZAxis = MagicMock()


def make_voxel_grid(dx=10, dy=10, dz=10, resolution=10, show_boxes=True):
    """
    Create a VoxelGrid with a mocked mesh and bounding box.
    Bypasses _classify_voxels by injecting filled keys directly.
    """
    with patch.dict('sys.modules', {'Rhino': MagicMock(), 'Rhino.Geometry': mock_rg}):
        from core.voxel_grid import VoxelGrid

        min_pt = MockPoint3d(0, 0, 0)
        max_pt = MockPoint3d(dx, dy, dz)
        bbox   = MockBoundingBox(min_pt, max_pt)
        mesh   = MockMesh(bbox)

        grid = VoxelGrid.__new__(VoxelGrid)
        grid._mesh      = mesh
        grid.resolution = resolution
        grid.show_boxes = show_boxes
        grid.origin     = min_pt

        longest         = max(dx, dy, dz)
        grid.voxel_size = longest / float(resolution)

        grid.nx = max(1, int(math.ceil(dx / grid.voxel_size)))
        grid.ny = max(1, int(math.ceil(dy / grid.voxel_size)))
        grid.nz = max(1, int(math.ceil(dz / grid.voxel_size)))

        # Inject a small set of filled keys directly
        keys = [(x, y, z)
                for x in range(grid.nx)
                for y in range(grid.ny)
                for z in range(grid.nz)]

        grid._filled_keys_ordered = sorted(keys)
        grid.filled_keys_set      = set(keys)
        grid._surface_keys        = None

        grid.centers = [
            MockPoint3d(
                grid.origin.X + (ix + 0.5) * grid.voxel_size,
                grid.origin.Y + (iy + 0.5) * grid.voxel_size,
                grid.origin.Z + (iz + 0.5) * grid.voxel_size,
            )
            for ix, iy, iz in grid._filled_keys_ordered
        ]

        return grid


# ── Resolution validation ─────────────────────────────────────────────────────

def test_resolution_zero_raises():
    with patch.dict('sys.modules', {'Rhino': MagicMock(), 'Rhino.Geometry': mock_rg}):
        from core.voxel_grid import VoxelGrid
        min_pt = MockPoint3d(0, 0, 0)
        max_pt = MockPoint3d(10, 10, 10)
        mesh   = MockMesh(MockBoundingBox(min_pt, max_pt))
        with pytest.raises(ValueError):
            VoxelGrid(mesh, resolution=0)

def test_resolution_negative_raises():
    with patch.dict('sys.modules', {'Rhino': MagicMock(), 'Rhino.Geometry': mock_rg}):
        from core.voxel_grid import VoxelGrid
        min_pt = MockPoint3d(0, 0, 0)
        max_pt = MockPoint3d(10, 10, 10)
        mesh   = MockMesh(MockBoundingBox(min_pt, max_pt))
        with pytest.raises(ValueError):
            VoxelGrid(mesh, resolution=-1)


# ── Voxel size calculation ────────────────────────────────────────────────────

def test_voxel_size_square_grid():
    grid = make_voxel_grid(dx=10, dy=10, dz=10, resolution=10)
    assert abs(grid.voxel_size - 1.0) < 1e-9

def test_voxel_size_rectangular_grid():
    grid = make_voxel_grid(dx=20, dy=10, dz=10, resolution=10)
    assert abs(grid.voxel_size - 2.0) < 1e-9

def test_voxel_size_uses_longest_axis():
    grid = make_voxel_grid(dx=5, dy=5, dz=30, resolution=10)
    assert abs(grid.voxel_size - 3.0) < 1e-9


# ── Grid dimensions ───────────────────────────────────────────────────────────

def test_grid_dimensions_square():
    grid = make_voxel_grid(dx=10, dy=10, dz=10, resolution=10)
    assert grid.nx == 10
    assert grid.ny == 10
    assert grid.nz == 10

def test_grid_dimensions_at_least_one():
    grid = make_voxel_grid(dx=10, dy=10, dz=10, resolution=1)
    assert grid.nx >= 1
    assert grid.ny >= 1
    assert grid.nz >= 1


# ── show_boxes flag ───────────────────────────────────────────────────────────

def test_show_boxes_true():
    grid = make_voxel_grid(show_boxes=True)
    assert grid.show_boxes is True

def test_show_boxes_false():
    grid = make_voxel_grid(show_boxes=False)
    assert grid.show_boxes is False


# ── filled_keys ordering ──────────────────────────────────────────────────────

def test_filled_keys_are_sorted():
    grid = make_voxel_grid(dx=10, dy=10, dz=10, resolution=3)
    keys = grid.filled_keys
    assert keys == sorted(keys)

def test_filled_keys_match_set():
    grid = make_voxel_grid(dx=10, dy=10, dz=10, resolution=3)
    assert set(grid.filled_keys) == grid.filled_keys_set


# ── face_neighbours ───────────────────────────────────────────────────────────

def test_face_neighbours_interior_voxel():
    grid = make_voxel_grid(dx=10, dy=10, dz=10, resolution=3)
    # (1,1,1) is interior — all 6 neighbours should be filled
    neighbours = grid.face_neighbours((1, 1, 1))
    assert len(neighbours) == 6

def test_face_neighbours_corner_voxel():
    grid = make_voxel_grid(dx=10, dy=10, dz=10, resolution=3)
    # (0,0,0) is a corner — only 3 neighbours exist in the grid
    neighbours = grid.face_neighbours((0, 0, 0))
    assert len(neighbours) == 3

def test_face_neighbours_only_returns_filled():
    grid = make_voxel_grid(dx=10, dy=10, dz=10, resolution=3)
    for nb in grid.face_neighbours((0, 0, 0)):
        assert nb in grid.filled_keys_set


# ── adjacency_count ───────────────────────────────────────────────────────────

def test_adjacency_count_interior():
    grid = make_voxel_grid(dx=10, dy=10, dz=10, resolution=3)
    assert grid.adjacency_count((1, 1, 1)) == 6

def test_adjacency_count_corner():
    grid = make_voxel_grid(dx=10, dy=10, dz=10, resolution=3)
    assert grid.adjacency_count((0, 0, 0)) == 3


# ── is_surface_voxel ──────────────────────────────────────────────────────────

def test_interior_voxel_not_surface():
    grid = make_voxel_grid(dx=10, dy=10, dz=10, resolution=3)
    assert not grid.is_surface_voxel((1, 1, 1))

def test_corner_voxel_is_surface():
    grid = make_voxel_grid(dx=10, dy=10, dz=10, resolution=3)
    assert grid.is_surface_voxel((0, 0, 0))


# ── surface_keys cache ────────────────────────────────────────────────────────

def test_surface_keys_returns_set():
    grid = make_voxel_grid(dx=10, dy=10, dz=10, resolution=3)
    assert isinstance(grid.surface_keys, set)

def test_surface_keys_cached():
    grid = make_voxel_grid(dx=10, dy=10, dz=10, resolution=3)
    first  = grid.surface_keys
    second = grid.surface_keys
    assert first is second

def test_surface_keys_excludes_interior():
    grid = make_voxel_grid(dx=10, dy=10, dz=10, resolution=3)
    assert (1, 1, 1) not in grid.surface_keys

def test_surface_keys_includes_corner():
    grid = make_voxel_grid(dx=10, dy=10, dz=10, resolution=3)
    assert (0, 0, 0) in grid.surface_keys
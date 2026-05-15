"""
tests/test_wfc_solver.py
==========================
Unit tests for core/wfc_solver.py
No Rhino dependency — pure Python.
"""

import sys
import os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'src'))

import pytest
from core.wfc_solver import WFCSolver, build_compat_table, compatible, DIRS, OPPOSITE


# ── Minimal tile mock ─────────────────────────────────────────────────────────

def make_tile(tile_type, neighbors_dict, max_count=-1, name=None):
    """
    Create a minimal tile mock.

    Parameters
    ----------
    tile_type      : str   single character
    neighbors_dict : dict  direction_key → frozenset of allowed tile_type chars
    max_count      : int
    name           : str
    """
    class Tile:
        pass
    t = Tile()
    t.tile_type = tile_type
    t.neighbors = {k: frozenset(v) for k, v in neighbors_dict.items()}
    t.max_count = max_count
    t.name      = name or tile_type
    return t

def all_directions(allowed):
    """Helper — tile that allows `allowed` in every direction."""
    return {d: allowed for d, *_ in DIRS}

def make_simple_tiles():
    """
    Two mutually compatible tiles A and B.
    A allows A and B in every direction.
    B allows A and B in every direction.
    """
    A = make_tile("A", all_directions("AB"), name="TileA")
    B = make_tile("B", all_directions("AB"), name="TileB")
    return [A, B]

def make_linear_keys(n):
    """n voxels in a line along x axis."""
    return [(i, 0, 0) for i in range(n)]


# ── OPPOSITE / DIRS constants ─────────────────────────────────────────────────

def test_opposite_is_symmetric():
    for d, opp in OPPOSITE.items():
        assert OPPOSITE[opp] == d

def test_dirs_has_six_entries():
    assert len(DIRS) == 6

def test_dirs_cover_all_axes():
    direction_names = [d for d, *_ in DIRS]
    assert set(direction_names) == {"px", "nx", "py", "ny", "pz", "nz"}


# ── compatible() ─────────────────────────────────────────────────────────────

def test_compatible_mutual():
    """A allows B in px, B allows A in nx → compatible."""
    A = make_tile("A", {**all_directions(""), "px": frozenset("B")})
    B = make_tile("B", {**all_directions(""), "nx": frozenset("A")})
    assert compatible(A, "px", B)

def test_compatible_one_way_fails():
    """A allows B in px but B does not allow A in nx → not compatible."""
    A = make_tile("A", {**all_directions(""), "px": frozenset("B")})
    B = make_tile("B", all_directions(""))   # B allows nothing
    assert not compatible(A, "px", B)

def test_compatible_self():
    """A allows A in all directions → compatible with itself."""
    A = make_tile("A", all_directions("A"))
    assert compatible(A, "px", A)


# ── build_compat_table() ──────────────────────────────────────────────────────

def test_compat_table_keys():
    tiles = make_simple_tiles()
    table = build_compat_table(tiles)
    # Should have one entry per (tile_index, direction) pair
    assert len(table) == len(tiles) * 6

def test_compat_table_values_are_frozensets():
    tiles = make_simple_tiles()
    table = build_compat_table(tiles)
    for v in table.values():
        assert isinstance(v, frozenset)

def test_compat_table_mutual_tiles():
    """Both tiles allow each other — every direction should have both as options."""
    tiles = make_simple_tiles()
    table = build_compat_table(tiles)
    for d, *_ in DIRS:
        assert table[(0, d)] == frozenset({0, 1})
        assert table[(1, d)] == frozenset({0, 1})


# ── WFCSolver construction ────────────────────────────────────────────────────

def test_solver_initial_wave():
    """Every voxel should start with all tiles as options."""
    tiles = make_simple_tiles()
    keys  = make_linear_keys(3)
    table = build_compat_table(tiles)
    ranking = [[0, 1, 2], [0, 1, 2]]

    solver = WFCSolver(keys, tiles, table, ranking)
    for key in keys:
        assert solver.wave[key] == {0, 1}

def test_solver_counts_start_at_zero():
    tiles = make_simple_tiles()
    keys  = make_linear_keys(3)
    table = build_compat_table(tiles)
    ranking = [[0, 1, 2], [0, 1, 2]]

    solver = WFCSolver(keys, tiles, table, ranking)
    assert solver.counts == [0, 0]
    assert solver.collapsed_count == 0


# ── solve() ───────────────────────────────────────────────────────────────────

def test_solve_collapses_all_voxels():
    """With mutually compatible tiles, all voxels should collapse."""
    tiles   = make_simple_tiles()
    keys    = make_linear_keys(5)
    table   = build_compat_table(tiles)
    ranking = [list(range(5)), list(range(5))]

    solver  = WFCSolver(keys, tiles, table, ranking)
    success = solver.solve(len(keys))

    assert success
    for key in keys:
        assert len(solver.wave[key]) == 1

def test_solve_returns_true_on_success():
    tiles   = make_simple_tiles()
    keys    = make_linear_keys(3)
    table   = build_compat_table(tiles)
    ranking = [list(range(3)), list(range(3))]

    solver  = WFCSolver(keys, tiles, table, ranking)
    result  = solver.solve(len(keys))
    assert result is True

def test_solve_respects_target_count():
    """Solver should stop when collapsed_count reaches target."""
    tiles   = make_simple_tiles()
    keys    = make_linear_keys(6)
    table   = build_compat_table(tiles)
    ranking = [list(range(6)), list(range(6))]

    solver = WFCSolver(keys, tiles, table, ranking)
    solver.solve(3)

    assert solver.collapsed_count >= 3

def test_solve_single_voxel():
    """A grid with one voxel should collapse immediately."""
    tiles   = make_simple_tiles()
    keys    = make_linear_keys(1)
    table   = build_compat_table(tiles)
    ranking = [[0], [0]]

    solver  = WFCSolver(keys, tiles, table, ranking)
    success = solver.solve(1)

    assert success
    assert len(solver.wave[keys[0]]) == 1


# ── max_count enforcement ─────────────────────────────────────────────────────

def test_max_count_respected():
    """Tile A with max_count=1 should appear at most once."""
    A = make_tile("A", all_directions("AB"), max_count=1, name="TileA")
    B = make_tile("B", all_directions("AB"), max_count=-1, name="TileB")
    tiles   = [A, B]
    keys    = make_linear_keys(4)
    table   = build_compat_table(tiles)
    ranking = [list(range(4)), list(range(4))]

    solver = WFCSolver(keys, tiles, table, ranking)
    solver.solve(len(keys))

    count_A = sum(
        1 for key in keys
        if solver.wave.get(key) == {0}
    )
    assert count_A <= 1

def test_max_count_zero_tile_never_placed():
    """A tile with max_count=0 should never appear in the solution."""
    A = make_tile("A", all_directions("AB"), max_count=0, name="TileA")
    B = make_tile("B", all_directions("AB"), max_count=-1, name="TileB")
    tiles   = [A, B]
    keys    = make_linear_keys(4)
    table   = build_compat_table(tiles)
    ranking = [list(range(4)), list(range(4))]

    solver = WFCSolver(keys, tiles, table, ranking)
    solver.solve(len(keys))

    for key in keys:
        opts = solver.wave.get(key, set())
        assert 0 not in opts or len(opts) > 1  # A should not be the sole option


# ── Propagation ───────────────────────────────────────────────────────────────

def test_propagation_removes_incompatible():
    """
    Tile A only allows A in px direction.
    Collapsing voxel 0 to A should force voxel 1 to A via propagation.
    """
    A = make_tile("A", {**all_directions("AB"), "px": frozenset("A")})
    B = make_tile("B", {**all_directions("AB"), "nx": frozenset("B")})
    tiles   = [A, B]
    keys    = make_linear_keys(2)
    table   = build_compat_table(tiles)
    # Force tile 0 (A) to be first pick for voxel 0
    ranking = [[0, 1], [1, 0]]

    solver = WFCSolver(keys, tiles, table, ranking)
    solver.solve(2)

    # Voxel 0 should be A (tile index 0)
    assert solver.wave[keys[0]] == {0}
    # Voxel 1 should also be forced to A by propagation
    assert solver.wave[keys[1]] == {0}


# ── Collapsed count ───────────────────────────────────────────────────────────

def test_collapsed_count_matches_wave():
    """collapsed_count should match the number of single-option voxels."""
    tiles   = make_simple_tiles()
    keys    = make_linear_keys(4)
    table   = build_compat_table(tiles)
    ranking = [list(range(4)), list(range(4))]

    solver = WFCSolver(keys, tiles, table, ranking)
    solver.solve(len(keys))

    actual_collapsed = sum(
        1 for key in keys if len(solver.wave.get(key, set())) == 1
    )
    assert solver.collapsed_count == actual_collapsed

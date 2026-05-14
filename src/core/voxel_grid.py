"""
core/voxel_grid.py
==================
VoxelGrid class — core data structure for the voxel plugin.

Previously defined inside the GH VoxelGrid component.
All components that consume a VoxelGrid import from here.
"""

import Rhino.Geometry as rg
import math


class VoxelGrid(object):
    """
    Represents a uniform 3-D voxel grid derived from a mesh or brep.

    Attributes
    ----------
    voxel_size : float
    nx, ny, nz : int
    origin     : Rhino.Geometry.Point3d
    show_boxes : bool   If True, as_geometry() returns Box objects; else Point3d.
    filled_keys        : list[(int,int,int)]  stable sorted list — read-only
    filled_keys_set    : set[(int,int,int)]   for O(1) membership tests
    centers            : list[Point3d]        parallel to filled_keys
    """

    def __init__(self, mesh, resolution=10, show_boxes=True):
        if int(resolution) < 1:
            raise ValueError(
                "resolution must be >= 1, got {}.".format(resolution))

        self._mesh      = mesh
        self.resolution = int(resolution)
        self.show_boxes = bool(show_boxes)

        bbox = mesh.GetBoundingBox(True)
        self.origin = bbox.Min
        dims = bbox.Max - bbox.Min

        longest = max(dims.X, dims.Y, dims.Z)
        self.voxel_size = longest / float(self.resolution)

        self.nx = max(1, int(math.ceil(dims.X / self.voxel_size)))
        self.ny = max(1, int(math.ceil(dims.Y / self.voxel_size)))
        self.nz = max(1, int(math.ceil(dims.Z / self.voxel_size)))

        self._filled_keys_ordered = []
        self.filled_keys_set      = set()
        self.centers              = []
        self._surface_keys        = None

        self._classify_voxels()

    # ── filled_keys property ──────────────────────────────────────────────────

    @property
    def filled_keys(self):
        """Stable sorted list of (ix, iy, iz) tuples — read-only."""
        return self._filled_keys_ordered

    # ── Inside/outside test ───────────────────────────────────────────────────

    @staticmethod
    def _count_crossings(mesh, pt, direction, nudge):
        MAX_ITER  = 64
        crossings = 0
        origin    = pt

        for _ in range(MAX_ITER):
            ray = rg.Ray3d(origin, direction)
            t   = rg.Intersect.Intersection.MeshRay(mesh, ray)
            if t < 0:
                break
            crossings += 1
            origin = rg.Point3d(
                origin.X + direction.X * (t + nudge),
                origin.Y + direction.Y * (t + nudge),
                origin.Z + direction.Z * (t + nudge),
            )

        return crossings

    @staticmethod
    def _is_inside(mesh, pt, voxel_size):
        DIRECTIONS = [
            rg.Vector3d( 1,  0,  0),
            rg.Vector3d(-1,  0,  0),
            rg.Vector3d( 0,  1,  0),
            rg.Vector3d( 0, -1,  0),
            rg.Vector3d( 0,  0,  1),
            rg.Vector3d( 0,  0, -1),
        ]
        nudge = voxel_size * 1e-4

        inside_votes = sum(
            1 for d in DIRECTIONS
            if VoxelGrid._count_crossings(mesh, pt, d, nudge) % 2 == 1
        )

        if inside_votes < 4:
            return False

        closest = mesh.ClosestPoint(pt)
        dist    = pt.DistanceTo(closest)
        if dist < voxel_size * 0.5:
            return mesh.IsPointInside(pt, voxel_size * 1e-4, True)

        return True

    # ── Voxel classification ──────────────────────────────────────────────────

    def _classify_voxels(self):
        vs     = self.voxel_size
        origin = self.origin
        mesh   = self._mesh

        passing = set()
        for ix in range(self.nx):
            for iy in range(self.ny):
                for iz in range(self.nz):
                    pt = rg.Point3d(
                        origin.X + (ix + 0.5) * vs,
                        origin.Y + (iy + 0.5) * vs,
                        origin.Z + (iz + 0.5) * vs,
                    )
                    if self._is_inside(mesh, pt, vs):
                        passing.add((ix, iy, iz))

        self._filled_keys_ordered = sorted(passing)
        self.filled_keys_set      = passing

        self.centers = [
            rg.Point3d(
                origin.X + (ix + 0.5) * vs,
                origin.Y + (iy + 0.5) * vs,
                origin.Z + (iz + 0.5) * vs,
            )
            for ix, iy, iz in self._filled_keys_ordered
        ]

    # ── Grid coordinate helpers ───────────────────────────────────────────────

    def key_to_center(self, key):
        ix, iy, iz = key
        vs = self.voxel_size
        return rg.Point3d(
            self.origin.X + (ix + 0.5) * vs,
            self.origin.Y + (iy + 0.5) * vs,
            self.origin.Z + (iz + 0.5) * vs,
        )

    def key_to_box(self, key):
        ix, iy, iz = key
        vs     = self.voxel_size
        corner = rg.Point3d(
            self.origin.X + ix * vs,
            self.origin.Y + iy * vs,
            self.origin.Z + iz * vs,
        )
        interval = rg.Interval(0, vs)
        plane    = rg.Plane(corner, rg.Vector3d.ZAxis)
        return rg.Box(plane, interval, interval, interval)

    def key_to_geometry(self, key):
        """Return Box or Point3d depending on show_boxes flag."""
        return self.key_to_box(key) if self.show_boxes else self.key_to_center(key)

    def as_geometry(self):
        """Return boxes or points for all filled voxels based on show_boxes."""
        return [self.key_to_geometry(k) for k in self._filled_keys_ordered]

    def as_boxes(self):
        return [self.key_to_box(k) for k in self._filled_keys_ordered]

    def as_points(self):
        return list(self.centers)

    # ── Neighbour helpers ─────────────────────────────────────────────────────

    def face_neighbours(self, key):
        ix, iy, iz = key
        candidates = [
            (ix+1, iy, iz), (ix-1, iy, iz),
            (ix, iy+1, iz), (ix, iy-1, iz),
            (ix, iy, iz+1), (ix, iy, iz-1),
        ]
        return [k for k in candidates if k in self.filled_keys_set]

    def adjacency_count(self, key):
        return len(self.face_neighbours(key))

    def is_surface_voxel(self, key):
        return self.adjacency_count(key) < 6

    @property
    def surface_keys(self):
        if self._surface_keys is None:
            self._surface_keys = {
                k for k in self._filled_keys_ordered
                if self.is_surface_voxel(k)
            }
        return self._surface_keys

    # ── Representation ────────────────────────────────────────────────────────

    def __repr__(self):
        return (
            "VoxelGrid({nx}x{ny}x{nz} | "
            "voxel_size={vs:.4f} | filled={n} | show_boxes={sb})".format(
                nx=self.nx, ny=self.ny, nz=self.nz,
                vs=self.voxel_size, n=len(self.filled_keys),
                sb=self.show_boxes,
            )
        )

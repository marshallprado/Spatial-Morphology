// -*- coding: utf-8 -*-
// Version 2.0.0
using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;
using System.Linq;

namespace SpatialMorphology
{
    public class VoxelGridComponent : GH_Component
    {
        // ── Constructor ───────────────────────────────────────────────────────
        public VoxelGridComponent()
            : base(
                "VoxelGrid",
                "VoxGrid",
                "Voxelizes a Brep or Mesh into a 3D grid aligned to a construction plane.\n\n" +
                "Version 2.0.0",
                "Spatial Morphology",
                "Setup")
        { }

        // ── GUID ──────────────────────────────────────────────────────────────
        public override Guid ComponentGuid =>
            new Guid("D1E2F3A4-B5C6-7890-DEFA-012345678903");

        // ── Icon ──────────────────────────────────────────────────────────────
        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "SpatialMorphology.Resources.VoxelGrid_24.png");
                return stream != null ? new Bitmap(stream) : null;
            }
        }

        // ── Parameters ────────────────────────────────────────────────────────
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGeometryParameter("geometry", "G",
                "Brep or Mesh to voxelize.",
                GH_ParamAccess.item);
            pManager.AddIntegerParameter("resolution", "R",
                "Number of voxels along the longest axis.",
                GH_ParamAccess.item, 10);
            pManager.AddBooleanParameter("show_boxes", "B",
                "If True, preview voxels as boxes. If False, preview as points.",
                GH_ParamAccess.item, true);
            pManager.AddPlaneParameter("plane", "P",
                "Construction plane to align the voxel grid to.\n" +
                "Default: World XY.",
                GH_ParamAccess.item, Plane.WorldXY);

            pManager[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("voxel_grid", "VG",
                "VoxelGrid object. Pass to SA components and AnalysisStack.",
                GH_ParamAccess.item);
            pManager.AddGeometryParameter("voxels", "V",
                "Voxel geometry for preview. Boxes or points based on show_boxes.",
                GH_ParamAccess.list);
            pManager.AddTextParameter("info", "I",
                "Grid summary.",
                GH_ParamAccess.item);
        }

        // ── Solve ─────────────────────────────────────────────────────────────
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // ── Collect inputs ────────────────────────────────────────────────
            GeometryBase geometry = null;
            int resolution = 10;
            bool showBoxes = true;
            Plane plane = Plane.WorldXY;

            if (!DA.GetData(0, ref geometry)) return;
            DA.GetData(1, ref resolution);
            DA.GetData(2, ref showBoxes);
            DA.GetData(3, ref plane);

            // ── Validate resolution ───────────────────────────────────────────
            if (resolution < 1)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "resolution must be >= 1.");
                return;
            }

            // ── Convert geometry to Mesh ──────────────────────────────────────
            Mesh mesh = null;

            if (geometry is Brep brep)
            {
                var meshes = Mesh.CreateFromBrep(brep,
                    MeshingParameters.QualityRenderMesh);
                mesh = new Mesh();
                foreach (var m in meshes)
                    mesh.Append(m);
            }
            else if (geometry is Mesh inputMesh)
            {
                mesh = inputMesh.DuplicateMesh();
            }
            else
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "geometry must be a Brep or Mesh.");
                return;
            }

            mesh.Faces.ConvertQuadsToTriangles();
            mesh.Normals.ComputeNormals();
            mesh.Compact();

            // ── Transform mesh to plane local space ───────────────────────────
            // Build transform from world to plane local space
            Transform worldToPlane = Transform.ChangeBasis(
                Plane.WorldXY, plane);

            // Build inverse transform from plane local to world
            Transform planeToWorld = Transform.ChangeBasis(
                plane, Plane.WorldXY);

            // Transform mesh into plane local space
            Mesh localMesh = mesh.DuplicateMesh();
            localMesh.Transform(worldToPlane);

            // ── Build VoxelGrid in local space ────────────────────────────────
            VoxelGrid voxelGrid;
            try
            {
                voxelGrid = new VoxelGrid(
                    localMesh, resolution, showBoxes, planeToWorld);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "VoxelGrid failed: " + ex.Message);
                return;
            }

            // ── Build preview geometry ────────────────────────────────────────
            var voxels = voxelGrid.AsGeometry();

            // ── Info ──────────────────────────────────────────────────────────
            string info = string.Format(
                "VoxelGrid({0}x{1}x{2} | voxel_size={3:F4} | filled={4} | " +
                "show_boxes={5} | plane={6})",
                voxelGrid.Nx, voxelGrid.Ny, voxelGrid.Nz,
                voxelGrid.VoxelSize,
                voxelGrid.FilledKeys.Count,
                voxelGrid.ShowBoxes,
                plane == Plane.WorldXY ? "WorldXY" : "Custom");

            // ── Output ────────────────────────────────────────────────────────
            DA.SetData(0, voxelGrid);
            DA.SetDataList(1, voxels);
            DA.SetData(2, info);
        }
    }

    // ── VoxelGrid data class ──────────────────────────────────────────────────
    public class VoxelGrid
    {
        // ── Properties ────────────────────────────────────────────────────────
        public double VoxelSize { get; private set; }
        public int Nx { get; private set; }
        public int Ny { get; private set; }
        public int Nz { get; private set; }
        public Point3d Origin { get; private set; }
        public bool ShowBoxes { get; private set; }
        public Transform PlaneToWorld { get; private set; }

        private List<(int, int, int)> _filledKeysOrdered;
        private HashSet<(int, int, int)> _filledKeysSet;
        private HashSet<(int, int, int)> _surfaceKeys;
        private HashSet<(int, int, int)> _belowGradeKeys;
        private List<Point3d> _centers;
        private Mesh _mesh;

        public IReadOnlyList<(int, int, int)> FilledKeys => _filledKeysOrdered;
        public HashSet<(int, int, int)> FilledKeysSet => _filledKeysSet;
        public IReadOnlyList<Point3d> Centers => _centers;
        public HashSet<(int, int, int)> BelowGradeKeys => _belowGradeKeys;

        // ── Constructor ───────────────────────────────────────────────────────
        public VoxelGrid(Mesh mesh, int resolution = 10,
            bool showBoxes = true,
            Transform? planeToWorld = null)
        {
            if (resolution < 1)
                throw new ArgumentException(
                    "resolution must be >= 1, got " + resolution);

            _mesh = mesh;
            ShowBoxes = showBoxes;
            PlaneToWorld = planeToWorld ?? Transform.Identity;

            var bbox = mesh.GetBoundingBox(true);
            Origin = bbox.Min;
            var dims = bbox.Max - bbox.Min;

            double longest = Math.Max(dims.X, Math.Max(dims.Y, dims.Z));
            VoxelSize = longest / (double)resolution;

            Nx = Math.Max(1, (int)Math.Ceiling(dims.X / VoxelSize));
            Ny = Math.Max(1, (int)Math.Ceiling(dims.Y / VoxelSize));
            Nz = Math.Max(1, (int)Math.Ceiling(dims.Z / VoxelSize));

            _filledKeysOrdered = new List<(int, int, int)>();
            _filledKeysSet = new HashSet<(int, int, int)>();
            _centers = new List<Point3d>();
            _surfaceKeys = null;
            _belowGradeKeys = new HashSet<(int, int, int)>();

            ClassifyVoxels();
        }

        // ── Inside/outside test ───────────────────────────────────────────────
        private int CountCrossings(Point3d pt, Vector3d direction, double nudge)
        {
            int crossings = 0;
            var origin = pt;

            for (int i = 0; i < 64; i++)
            {
                var ray = new Ray3d(origin, direction);
                double t = Rhino.Geometry.Intersect.Intersection.MeshRay(_mesh, ray);

                if (t < 0) break;

                crossings++;
                origin = new Point3d(
                    origin.X + direction.X * (t + nudge),
                    origin.Y + direction.Y * (t + nudge),
                    origin.Z + direction.Z * (t + nudge));
            }

            return crossings;
        }

        private bool IsInside(Point3d pt)
        {
            double nudge = VoxelSize * 1e-4;

            int insideVotes = 0;
            if (CountCrossings(pt, new Vector3d(1, 0, 0), nudge) % 2 == 1) insideVotes++;
            if (CountCrossings(pt, new Vector3d(-1, 0, 0), nudge) % 2 == 1) insideVotes++;
            if (CountCrossings(pt, new Vector3d(0, 1, 0), nudge) % 2 == 1) insideVotes++;
            if (CountCrossings(pt, new Vector3d(0, -1, 0), nudge) % 2 == 1) insideVotes++;
            if (CountCrossings(pt, new Vector3d(0, 0, 1), nudge) % 2 == 1) insideVotes++;
            if (CountCrossings(pt, new Vector3d(0, 0, -1), nudge) % 2 == 1) insideVotes++;

            if (insideVotes < 4) return false;

            var closest = _mesh.ClosestPoint(pt);
            double dist = pt.DistanceTo(closest);
            if (dist < VoxelSize * 0.5)
                return _mesh.IsPointInside(pt, VoxelSize * 1e-4, true);

            return true;
        }

        // ── Voxel classification ──────────────────────────────────────────────
        private void ClassifyVoxels()
        {
            double vs = VoxelSize;
            var origin = Origin;
            var passing = new HashSet<(int, int, int)>();

            for (int ix = 0; ix < Nx; ix++)
                for (int iy = 0; iy < Ny; iy++)
                    for (int iz = 0; iz < Nz; iz++)
                    {
                        var pt = new Point3d(
                            origin.X + (ix + 0.5) * vs,
                            origin.Y + (iy + 0.5) * vs,
                            origin.Z + (iz + 0.5) * vs);

                        if (IsInside(pt))
                            passing.Add((ix, iy, iz));
                    }

            _filledKeysOrdered = passing
                .OrderBy(k => k.Item1)
                .ThenBy(k => k.Item2)
                .ThenBy(k => k.Item3)
                .ToList();

            _filledKeysSet = passing;

            // Centers stored in world space
            _centers = _filledKeysOrdered.Select(k =>
            {
                var localPt = new Point3d(
                    origin.X + (k.Item1 + 0.5) * vs,
                    origin.Y + (k.Item2 + 0.5) * vs,
                    origin.Z + (k.Item3 + 0.5) * vs);
                localPt.Transform(PlaneToWorld);
                return localPt;
            }).ToList();

            // ── Compute below-grade keys ──────────────────────────────────────────────
            // In local grid space the construction plane is always at Z = 0.
            // A voxel is below grade if its local Z centre is negative.
            // localZ = Origin.Z + (iz + 0.5) * VoxelSize
            _belowGradeKeys = new HashSet<(int, int, int)>();
            foreach (var k in _filledKeysOrdered)
            {
                double localZ = Origin.Z + (k.Item3 + 0.5) * VoxelSize;
                if (localZ < 0.0)
                    _belowGradeKeys.Add(k);
            }
        }

        // ── Grid coordinate helpers ───────────────────────────────────────────

        // Returns world-space centre point
        public Point3d KeyToCenter((int, int, int) key)
        {
            double vs = VoxelSize;
            var localPt = new Point3d(
                Origin.X + (key.Item1 + 0.5) * vs,
                Origin.Y + (key.Item2 + 0.5) * vs,
                Origin.Z + (key.Item3 + 0.5) * vs);
            localPt.Transform(PlaneToWorld);
            return localPt;
        }

        // Returns world-space box
        public Box KeyToBox((int, int, int) key)
        {
            double vs = VoxelSize;

            // Build box in local space first
            var localCorner = new Point3d(
                Origin.X + key.Item1 * vs,
                Origin.Y + key.Item2 * vs,
                Origin.Z + key.Item3 * vs);

            // Transform corner to world space
            var worldCorner = new Point3d(localCorner);
            worldCorner.Transform(PlaneToWorld);

            // Build plane at world corner aligned to construction plane
            // Extract axes from PlaneToWorld transform
            var xAxis = new Vector3d(
                PlaneToWorld.M00, PlaneToWorld.M10, PlaneToWorld.M20);
            var yAxis = new Vector3d(
                PlaneToWorld.M01, PlaneToWorld.M11, PlaneToWorld.M21);

            var boxPlane = new Plane(worldCorner, xAxis, yAxis);
            var interval = new Interval(0, vs);

            return new Box(boxPlane, interval, interval, interval);
        }

        public GeometryBase KeyToGeometry((int, int, int) key)
        {
            if (ShowBoxes)
            {
                var box = KeyToBox(key);
                return box.ToBrep();
            }
            return new Rhino.Geometry.Point(KeyToCenter(key));
        }

        public List<GeometryBase> AsGeometry()
        {
            var result = new List<GeometryBase>();
            foreach (var key in _filledKeysOrdered)
                result.Add(KeyToGeometry(key));
            return result;
        }

        // ── Below-grade helper ────────────────────────────────────────────────────
        /// <summary>
        /// Returns true if the voxel centre is below the construction plane (Z=0 in local space).
        /// Computed once at grid creation — O(1) lookup.
        /// </summary>
        public bool IsBelowGrade((int, int, int) key)
        {
            return _belowGradeKeys.Contains(key);
        }

        // ── Neighbour helpers ─────────────────────────────────────────────────
        public List<(int, int, int)> FaceNeighbours((int, int, int) key)
        {
            int ix = key.Item1, iy = key.Item2, iz = key.Item3;
            var result = new List<(int, int, int)>();

            (int, int, int)[] candidates = new (int, int, int)[]
            {
                (ix+1, iy, iz), (ix-1, iy, iz),
                (ix, iy+1, iz), (ix, iy-1, iz),
                (ix, iy, iz+1), (ix, iy, iz-1),
            };

            foreach (var c in candidates)
                if (_filledKeysSet.Contains(c))
                    result.Add(c);
            return result;
        }

        public int AdjacencyCount((int, int, int) key) =>
            FaceNeighbours(key).Count;

        public bool IsSurfaceVoxel((int, int, int) key) =>
            AdjacencyCount(key) < 6;

        public HashSet<(int, int, int)> SurfaceKeys
        {
            get
            {
                if (_surfaceKeys == null)
                {
                    _surfaceKeys = new HashSet<(int, int, int)>();
                    foreach (var k in _filledKeysOrdered)
                        if (IsSurfaceVoxel(k))
                            _surfaceKeys.Add(k);
                }
                return _surfaceKeys;
            }
        }

        public override string ToString()
        {
            return string.Format(
                "VoxelGrid({0}x{1}x{2} | voxel_size={3:F4} | filled={4} | show_boxes={5})",
                Nx, Ny, Nz, VoxelSize, _filledKeysOrdered.Count, ShowBoxes);
        }
    }
}
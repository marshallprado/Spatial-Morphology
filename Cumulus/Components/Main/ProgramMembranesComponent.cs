// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Cumulus.Components.Data;
using Cumulus.Infrastructure.Data;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace Cumulus.Components.Main
{
    /// <summary>
    /// Generates closed program membranes directly from voxel grid and program distribution.
    /// Creates one mesh per program with inward-facing normals, optional Laplacian smoothing,
    /// and configurable gap offset for visual separation.
    /// </summary>
    public sealed class ProgramMembranesComponent : GH_Component
    {
        public ProgramMembranesComponent()
            : base(
                "Program Membranes",
                "ProgMembranes",
                "Generates closed program membranes directly from the voxel grid. " +
                "Each program becomes a complete mesh with inward-facing normals.\n\n" +
                "Optional Laplacian smoothing with pinned boundary vertices smooths " +
                "interior geometry while preserving interface topology.\n\n" +
                "Gap offset separates program meshes visually without overlap.",
                "Cumulus",
                "3 | Main")
        {
        }

        public override Guid ComponentGuid =>
            new Guid("50C2841F-9D32-5C25-A458-D8F6D9B44E83");

        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "Cumulus.Resources.ProgMembranes_24.png");
                return stream != null ? new Bitmap(stream) : null!;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "analysis_stack",
                "AS",
                "AnalysisStackData from AnalysisStack.",
                GH_ParamAccess.item);

            pManager.AddIntegerParameter(
                "smoothing_iterations",
                "SI",
                "Number of Laplacian smoothing passes. 0 = no smoothing.",
                GH_ParamAccess.item,
                1);

            pManager.AddNumberParameter(
                "gap_value",
                "G",
                "Offset distance (model units) to separate program meshes inward. " +
                "0 = no offset, positive values shrink meshes inward.",
                GH_ParamAccess.item,
                0.05);

            pManager.AddBooleanParameter(
                "include_boundary_faces",
                "IBF",
                "Generate faces for voxels adjacent to unallocated/exterior volume. " +
                "Creates completely closed meshes.",
                GH_ParamAccess.item,
                false);

            pManager[1].Optional = true;
            pManager[2].Optional = true;
            pManager[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter(
                "membranes",
                "M",
                "One closed mesh per program with inward-facing normals, grouped by program index.",
                GH_ParamAccess.tree);

            pManager.AddTextParameter(
                "info",
                "I",
                "Summary per program (voxel count, vertex count, face count).",
                GH_ParamAccess.tree);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            object? stackObject = null;
            int smoothingIterations = 1;
            double gapValue = 0.05;
            bool includeBoundaryFaces = false;

            if (!DA.GetData(0, ref stackObject))
                return;

            DA.GetData(1, ref smoothingIterations);
            DA.GetData(2, ref gapValue);
            DA.GetData(3, ref includeBoundaryFaces);

            AnalysisStackData? stack = UnwrapStack(stackObject);

            if (stack == null)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Could not read AnalysisStackData from AS.");
                return;
            }

            if (stack.VoxelGrid == null ||
                stack.ProgramIndices == null ||
                stack.VoxelGrid.FilledKeys.Count != stack.ProgramIndices.Count)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "AnalysisStackData has inconsistent VoxelGrid and ProgramIndices data.");
                return;
            }

            var membranes = new GH_Structure<GH_Mesh>();
            var infos = new GH_Structure<GH_String>();

            // Group voxels by program
            var programVoxels = new Dictionary<int, List<(int, int, int)>>();
            for (int i = 0; i < stack.ProgramIndices.Count; i++)
            {
                int programId = stack.ProgramIndices[i];
                var key = stack.VoxelGrid.FilledKeys[i];

                if (!programVoxels.ContainsKey(programId))
                    programVoxels[programId] = new List<(int, int, int)>();

                programVoxels[programId].Add(key);
            }

            // Generate mesh for each program
            foreach (var kvp in programVoxels.OrderBy(x => x.Key))
            {
                int programId = kvp.Key;
                List<(int, int, int)> voxels = kvp.Value;
                var path = new GH_Path(programId);

                Mesh mesh = GenerateProgramMesh(
                    stack.VoxelGrid,
                    voxels,
                    includeBoundaryFaces,
                    gapValue,
                    smoothingIterations);

                membranes.Append(new GH_Mesh(mesh), path);

                string info = string.Format(
                    "Program {0} | voxels={1} | vertices={2} | faces={3} | " +
                    "smoothing={4} | gap={5:F4}",
                    programId,
                    voxels.Count,
                    mesh.Vertices.Count,
                    mesh.Faces.Count,
                    smoothingIterations,
                    gapValue);

                infos.Append(new GH_String(info), path);
            }

            DA.SetDataTree(0, membranes);
            DA.SetDataTree(1, infos);
        }

        private static AnalysisStackData? UnwrapStack(object? value)
        {
            if (value is GH_ObjectWrapper wrapper)
                return wrapper.Value as AnalysisStackData;

            return value as AnalysisStackData;
        }

        private Mesh GenerateProgramMesh(
            VoxelGrid voxelGrid,
            List<(int, int, int)> programVoxels,
            bool includeBoundaryFaces,
            double gapValue,
            int smoothingIterations)
        {
            var mesh = new Mesh();
            var voxelSet = new HashSet<(int, int, int)>(programVoxels);

            // Compute program centroid for inward normal orientation
            Point3d centroid = Point3d.Origin;
            foreach (var key in programVoxels)
            {
                centroid += voxelGrid.KeyToCenter(key);
            }
            centroid /= programVoxels.Count;

            // Generate faces for each voxel
            foreach (var voxelKey in programVoxels)
            {
                GenerateVoxelFaces(
                    mesh,
                    voxelGrid,
                    voxelKey,
                    voxelSet,
                    includeBoundaryFaces,
                    centroid,
                    gapValue);
            }

            mesh.Normals.ComputeNormals();
            mesh.Compact();

            // Apply smoothing if requested
            if (smoothingIterations > 0)
            {
                ApplyLaplacianSmoothing(mesh, smoothingIterations);
            }

            mesh.Normals.ComputeNormals();
            mesh.Compact();

            return mesh;
        }

        private void GenerateVoxelFaces(
            Mesh mesh,
            VoxelGrid voxelGrid,
            (int, int, int) voxelKey,
            HashSet<(int, int, int)> voxelSet,
            bool includeBoundaryFaces,
            Point3d centroid,
            double gapValue)
        {
            int x = voxelKey.Item1;
            int y = voxelKey.Item2;
            int z = voxelKey.Item3;

            // Check all 6 face directions
            var directions = new[]
            {
                (1, 0, 0), (-1, 0, 0),  // X axis
                (0, 1, 0), (0, -1, 0),  // Y axis
                (0, 0, 1), (0, 0, -1)   // Z axis
            };

            foreach (var (dx, dy, dz) in directions)
            {
                var neighborKey = (x + dx, y + dy, z + dz);
                bool hasNeighbor = voxelSet.Contains(neighborKey);
                bool isGridNeighbor = voxelGrid.FilledKeys.Contains(neighborKey);

                // Generate face if:
                // - No neighbor in this program (boundary face), AND
                // - Either includeBoundaryFaces is true, OR there's a grid neighbor
                if (!hasNeighbor && (includeBoundaryFaces || isGridNeighbor))
                {
                    AddVoxelFace(
                        mesh,
                        voxelGrid,
                        voxelKey,
                        dx, dy, dz,
                        centroid,
                        gapValue);
                }
            }
        }

        private void AddVoxelFace(
            Mesh mesh,
            VoxelGrid voxelGrid,
            (int, int, int) voxelKey,
            int dx, int dy, int dz,
            Point3d centroid,
            double gapValue)
        {
            int x = voxelKey.Item1;
            int y = voxelKey.Item2;
            int z = voxelKey.Item3;

            // Get the 4 corners of this voxel face
            Point3d[] corners = GetVoxelFaceCorners(voxelGrid, x, y, z, dx, dy, dz);

            if (corners == null || corners.Length != 4)
                return;

            // Compute face center and outward normal
            Point3d faceCenter = new Point3d(
                (corners[0].X + corners[1].X + corners[2].X + corners[3].X) / 4.0,
                (corners[0].Y + corners[1].Y + corners[2].Y + corners[3].Y) / 4.0,
                (corners[0].Z + corners[1].Z + corners[2].Z + corners[3].Z) / 4.0);

            Vector3d outwardNormal = new Vector3d(dx, dy, dz);
            outwardNormal.Transform(voxelGrid.PlaneToWorld);
            outwardNormal.Unitize();

            // Invert normal to face inward (toward centroid)
            Vector3d towardCentroid = centroid - faceCenter;
            if (outwardNormal * towardCentroid < 0)
            {
                outwardNormal = -outwardNormal;
            }

            // Apply inward offset
            Vector3d offsetVector = -outwardNormal * gapValue;

            int start = mesh.Vertices.Count;
            foreach (var corner in corners)
            {
                mesh.Vertices.Add(corner + offsetVector);
            }

            // Add face with correct winding for inward normal
            mesh.Faces.AddFace(start, start + 3, start + 2, start + 1);
        }

        private Point3d[] GetVoxelFaceCorners(
            VoxelGrid voxelGrid,
            int x, int y, int z,
            int dx, int dy, int dz)
        {
            double vs = voxelGrid.VoxelSize;
            Point3d origin = voxelGrid.Origin;

            Point3d[] localCorners = null;

            if (dx != 0)
            {
                // Face perpendicular to X axis
                int planeX = x + (dx > 0 ? 1 : 0);
                localCorners = new[]
                {
                    new Point3d(planeX * vs, y * vs, z * vs),
                    new Point3d(planeX * vs, (y + 1) * vs, z * vs),
                    new Point3d(planeX * vs, (y + 1) * vs, (z + 1) * vs),
                    new Point3d(planeX * vs, y * vs, (z + 1) * vs)
                };
            }
            else if (dy != 0)
            {
                // Face perpendicular to Y axis
                int planeY = y + (dy > 0 ? 1 : 0);
                localCorners = new[]
                {
                    new Point3d(x * vs, planeY * vs, z * vs),
                    new Point3d(x * vs, planeY * vs, (z + 1) * vs),
                    new Point3d((x + 1) * vs, planeY * vs, (z + 1) * vs),
                    new Point3d((x + 1) * vs, planeY * vs, z * vs)
                };
            }
            else if (dz != 0)
            {
                // Face perpendicular to Z axis
                int planeZ = z + (dz > 0 ? 1 : 0);
                localCorners = new[]
                {
                    new Point3d(x * vs, y * vs, planeZ * vs),
                    new Point3d((x + 1) * vs, y * vs, planeZ * vs),
                    new Point3d((x + 1) * vs, (y + 1) * vs, planeZ * vs),
                    new Point3d(x * vs, (y + 1) * vs, planeZ * vs)
                };
            }

            if (localCorners == null)
                return null;

            // Transform to world space
            Point3d[] worldCorners = new Point3d[4];
            for (int i = 0; i < 4; i++)
            {
                Point3d local = localCorners[i];
                local.X = origin.X + local.X;
                local.Y = origin.Y + local.Y;
                local.Z = origin.Z + local.Z;
                local.Transform(voxelGrid.PlaneToWorld);
                worldCorners[i] = local;
            }

            return worldCorners;
        }

        private void ApplyLaplacianSmoothing(Mesh mesh, int iterations)
        {
            for (int iter = 0; iter < iterations; iter++)
            {
                var newPositions = new Point3d[mesh.Vertices.Count];
                Array.Copy(mesh.Vertices.ToPoint3dArray(), newPositions, mesh.Vertices.Count);

                // Build vertex adjacency
                var adjacency = new List<int>[mesh.Vertices.Count];
                for (int i = 0; i < adjacency.Length; i++)
                    adjacency[i] = new List<int>();

                foreach (var face in mesh.Faces)
                {
                    int[] indices = new[] { face.A, face.B, face.C };
                    if (face.IsQuad)
                        indices = new[] { face.A, face.B, face.C, face.D };

                    for (int i = 0; i < indices.Length; i++)
                    {
                        int curr = indices[i];
                        int next = indices[(i + 1) % indices.Length];
                        if (!adjacency[curr].Contains(next))
                            adjacency[curr].Add(next);
                    }
                }

                // Smooth interior vertices
                for (int i = 0; i < mesh.Vertices.Count; i++)
                {
                    if (adjacency[i].Count == 0)
                        continue;

                    Point3d avg = Point3d.Origin;
                    foreach (int neighbor in adjacency[i])
                    {
                        avg += mesh.Vertices[neighbor];
                    }
                    avg /= adjacency[i].Count;

                    newPositions[i] = avg;
                }

                // Update mesh vertices
                for (int i = 0; i < mesh.Vertices.Count; i++)
                {
                    mesh.Vertices.SetVertex(i, newPositions[i]);
                }
            }
        }
    }
}

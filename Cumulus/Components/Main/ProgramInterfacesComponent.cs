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

namespace Cumulus
{
    /// <summary>
    /// Extracts exact, unoffset shared voxel faces between allocated programs.
    /// Program side boundaries toward unallocated/exterior volume remain open.
    /// Optional horizontal caps close only top and bottom open program faces.
    /// </summary>
    public sealed class ProgramInterfacesComponent : GH_Component
    {
        private const int CoreProgramIndex = -2;

        public ProgramInterfacesComponent()
            : base(
                "Program Interfaces",
                "ProgInterfaces",
                "Extracts the exact canonical shared-face complex between allocated " +
                "program voxels.\n\n" +
                "Interfaces facing unallocated voxels or outside the VoxelGrid are " +
                "intentionally omitted, leaving those boundaries open.\n\n" +
                "Use Program Membranes later to derive smoothed, offset, " +
                "program-colored display meshes.",
                "Cumulus",
                "3 | Main")
        {
        }

        public override Guid ComponentGuid =>
            new Guid("40B1731F-8C21-4B14-9347-C7E5C9A33D72");

        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "Cumulus.Resources.ProgInterface_24.png");
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

            pManager.AddBooleanParameter(
                "include_core",
                "IC",
                "Create program-to-core interfaces. Core is retained as an " +
                "adjacency condition, not as a separate interface membrane.",
                GH_ParamAccess.item,
                true);

            pManager.AddBooleanParameter(
                "cap_horizontal_open_faces",
                "HC",
                "Create top and bottom cap faces where a regular program voxel " +
                "faces unallocated volume, excluded core, or the exterior of " +
                "the VoxelGrid domain. Vertical side boundaries remain open.",
                GH_ParamAccess.item,
                true);

            pManager[1].Optional = true;
            pManager[2].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter(
                "canonical_interface_mesh",
                "IM",
                "One exact, unoffset, potentially non-manifold mesh complex " +
                "containing shared allocated-program faces and optional horizontal open-boundary caps.",
                GH_ParamAccess.item);

            pManager.AddMeshParameter(
                "interface_faces",
                "IF",
                "Exact unoffset interface meshes, grouped by program pair. " +
                "Horizontal open-boundary caps use ProgramB index -1. These meshes " +
                "are stable source geometry for later wall, circulation, and structural refinement.",
                GH_ParamAccess.tree);

            pManager.AddGenericParameter(
                "interface_data",
                "ID",
                "ProgramInterfaceData records, grouped by program pair. Each " +
                "record preserves both program/voxel sides, center, normal, grid axis, " +
                "grid Z, exact face area, and open-boundary cap status.",
                GH_ParamAccess.tree);

            pManager.AddGenericParameter(
                "adjacency_graph",
                "IG",
                "ProgramAdjacencyGraphData containing direct program/core " +
                "adjacencies weighted by canonical shared-face area.",
                GH_ParamAccess.item);

            pManager.AddNumberParameter(
                "interface_areas",
                "IA",
                "Total canonical face area, grouped by program pair. ProgramB index -1 denotes horizontal caps.",
                GH_ParamAccess.tree);

            pManager.AddTextParameter(
                "report",
                "R",
                "Extraction summary.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            object? stackObject = null;
            bool includeCore = true;
            bool capHorizontalOpenFaces = true;

            if (!DA.GetData(0, ref stackObject))
                return;

            DA.GetData(1, ref includeCore);
            DA.GetData(2, ref capHorizontalOpenFaces);

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

            var keys = stack.VoxelGrid.FilledKeys
                .Select(key => new ProgramInterfaceVoxelKey(
                    key.Item1,
                    key.Item2,
                    key.Item3))
                .ToList();

            ProgramInterfaceExtractionResult extraction;

            try
            {
                extraction = ProgramInterfaceExtractionEngine.Run(
                    new ProgramInterfaceExtractionRequest(
                        keys,
                        stack.ProgramIndices,
                        includeCore,
                        capHorizontalOpenFaces));
            }
            catch (Exception exception)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Program-interface extraction failed: " + exception.Message);
                return;
            }

            if (extraction.Faces.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    capHorizontalOpenFaces
                        ? "No program-to-program or program-to-core interfaces were found. " +
                          "Only horizontal open-boundary cap faces may be present."
                        : "No direct allocated-program interfaces were found. " +
                          "Faces toward unallocated voxels and exterior domain boundaries " +
                          "are open.");
            }

            Mesh canonicalMesh = BuildMesh(stack.VoxelGrid, extraction.Faces);

            var interfaceMeshes = new GH_Structure<GH_Mesh>();
            var interfaceData = new GH_Structure<GH_ObjectWrapper>();
            var interfaceAreas = new GH_Structure<GH_Number>();
            var graphEdges = new List<ProgramAdjacencyEdgeData>();

            foreach (var group in extraction.Faces
                .GroupBy(face => Tuple.Create(face.ProgramA, face.ProgramB))
                .OrderBy(group => group.Key.Item1)
                .ThenBy(group => group.Key.Item2))
            {
                int programA = group.Key.Item1;
                int programB = group.Key.Item2;
                var path = new GH_Path(programA, programB);
                List<ProgramInterfaceFace> pairFaces = group.ToList();

                Mesh pairMesh = BuildMesh(stack.VoxelGrid, pairFaces);
                interfaceMeshes.Append(new GH_Mesh(pairMesh), path);

                double pairArea =
                    pairFaces.Count *
                    stack.VoxelGrid.VoxelSize *
                    stack.VoxelGrid.VoxelSize;

                interfaceAreas.Append(new GH_Number(pairArea), path);

                foreach (ProgramInterfaceFace face in pairFaces)
                {
                    interfaceData.Append(
                        new GH_ObjectWrapper(
                            CreateInterfaceData(stack, face)),
                        path);
                }

                // Open-boundary caps are geometry only; they never become
                // program-adjacency graph edges.
                if (programB != -1)
                {
                    graphEdges.Add(new ProgramAdjacencyEdgeData(
                        programA,
                        programB,
                        GetProgramName(stack.Programs, programA),
                        GetProgramName(stack.Programs, programB),
                        pairFaces.Count,
                        pairArea));
                }
            }

            var graph = new ProgramAdjacencyGraphData(graphEdges);

            DA.SetData(0, new GH_Mesh(canonicalMesh));
            DA.SetDataTree(1, interfaceMeshes);
            DA.SetDataTree(2, interfaceData);
            DA.SetData(3, new GH_ObjectWrapper(graph));
            DA.SetDataTree(4, interfaceAreas);
            DA.SetData(5, BuildReport(
                extraction,
                includeCore,
                capHorizontalOpenFaces,
                stack.VoxelGrid.VoxelSize));
        }

        private static AnalysisStackData? UnwrapStack(object? value)
        {
            if (value is GH_ObjectWrapper wrapper)
                return wrapper.Value as AnalysisStackData;

            return value as AnalysisStackData;
        }

        private static string GetProgramName(
            IReadOnlyList<ProgramDefinition> programs,
            int programIndex)
        {
            if (programIndex == CoreProgramIndex)
                return "Core";

            if (programIndex == -1)
                return "Open Boundary";

            return programIndex >= 0 && programIndex < programs.Count
                ? programs[programIndex].Name
                : "Program " + programIndex;
        }

        private static ProgramInterfaceData CreateInterfaceData(
            AnalysisStackData stack,
            ProgramInterfaceFace face)
        {
            Point3d[] corners = GetWorldFaceCorners(stack.VoxelGrid, face);
            Point3d center = new Point3d(
                corners.Average(point => point.X),
                corners.Average(point => point.Y),
                corners.Average(point => point.Z));

            Vector3d normal = FaceNormal(stack.VoxelGrid, face.Axis, face.Direction);

            return new ProgramInterfaceData(
                face.ProgramA,
                face.ProgramB,
                GetProgramName(stack.Programs, face.ProgramA),
                GetProgramName(stack.Programs, face.ProgramB),
                face.VoxelA,
                face.VoxelB,
                face.Axis,
                face.Direction,
                face.KeyA.Z,
                center,
                normal,
                stack.VoxelGrid.VoxelSize * stack.VoxelGrid.VoxelSize,
                face.ProgramA == CoreProgramIndex ||
                face.ProgramB == CoreProgramIndex,
                face.Kind == ProgramInterfaceFaceKind.HorizontalOpenBoundaryCap,
                face.IsExteriorCap);
        }

        private static Mesh BuildMesh(
            VoxelGrid grid,
            IEnumerable<ProgramInterfaceFace> faces)
        {
            var mesh = new Mesh();

            foreach (ProgramInterfaceFace face in faces)
            {
                Point3d[] corners = GetWorldFaceCorners(grid, face);
                int start = mesh.Vertices.Count;

                mesh.Vertices.Add(corners[0]);
                mesh.Vertices.Add(corners[1]);
                mesh.Vertices.Add(corners[2]);
                mesh.Vertices.Add(corners[3]);
                mesh.Faces.AddFace(start, start + 1, start + 2, start + 3);
            }

            mesh.Normals.ComputeNormals();
            mesh.Compact();

            return mesh;
        }

        private static Point3d[] GetWorldFaceCorners(
            VoxelGrid grid,
            ProgramInterfaceFace face)
        {
            int x = face.KeyA.X;
            int y = face.KeyA.Y;
            int z = face.KeyA.Z;
            int side = face.Direction > 0 ? 1 : 0;

            Point3d[] localCorners;

            if (face.Axis == 0)
            {
                int planeX = x + side;

                localCorners = face.Direction > 0
                    ? new[]
                    {
                        new Point3d(planeX, y, z),
                        new Point3d(planeX, y + 1, z),
                        new Point3d(planeX, y + 1, z + 1),
                        new Point3d(planeX, y, z + 1)
                    }
                    : new[]
                    {
                        new Point3d(planeX, y, z),
                        new Point3d(planeX, y, z + 1),
                        new Point3d(planeX, y + 1, z + 1),
                        new Point3d(planeX, y + 1, z)
                    };
            }
            else if (face.Axis == 1)
            {
                int planeY = y + side;

                localCorners = face.Direction > 0
                    ? new[]
                    {
                        new Point3d(x, planeY, z),
                        new Point3d(x, planeY, z + 1),
                        new Point3d(x + 1, planeY, z + 1),
                        new Point3d(x + 1, planeY, z)
                    }
                    : new[]
                    {
                        new Point3d(x, planeY, z),
                        new Point3d(x + 1, planeY, z),
                        new Point3d(x + 1, planeY, z + 1),
                        new Point3d(x, planeY, z + 1)
                    };
            }
            else
            {
                int planeZ = z + side;

                localCorners = face.Direction > 0
                    ? new[]
                    {
                        new Point3d(x, y, planeZ),
                        new Point3d(x + 1, y, planeZ),
                        new Point3d(x + 1, y + 1, planeZ),
                        new Point3d(x, y + 1, planeZ)
                    }
                    : new[]
                    {
                        new Point3d(x, y, planeZ),
                        new Point3d(x, y + 1, planeZ),
                        new Point3d(x + 1, y + 1, planeZ),
                        new Point3d(x + 1, y, planeZ)
                    };
            }

            for (int index = 0; index < localCorners.Length; index++)
            {
                Point3d local = localCorners[index];

                local.X = grid.Origin.X + local.X * grid.VoxelSize;
                local.Y = grid.Origin.Y + local.Y * grid.VoxelSize;
                local.Z = grid.Origin.Z + local.Z * grid.VoxelSize;
                local.Transform(grid.PlaneToWorld);

                localCorners[index] = local;
            }

            return localCorners;
        }

        private static Vector3d FaceNormal(
            VoxelGrid grid,
            int axis,
            int direction)
        {
            Vector3d local = axis == 0
                ? Vector3d.XAxis
                : axis == 1
                    ? Vector3d.YAxis
                    : Vector3d.ZAxis;

            local *= direction;
            local.Transform(grid.PlaneToWorld);
            local.Unitize();

            return local;
        }

        private static string BuildReport(
            ProgramInterfaceExtractionResult extraction,
            bool includeCore,
            bool capHorizontalOpenFaces,
            double voxelSize)
        {
            double faceArea = voxelSize * voxelSize;
            double totalArea = extraction.Faces.Count * faceArea;

            return string.Format(
                "Program Interfaces | faces={0} | canonical_area={1:F3} | " +
                "adjacencies={2} | include_core={3} | horizontal_caps={4}\n" +
                "Horizontal caps: unallocated={5}, exterior={6}, core_disabled={7}\n" +
                "Open side contacts omitted: unallocated={8}, exterior={9}, " +
                "core_disabled={10}, same_program={11}",
                extraction.Faces.Count,
                totalArea,
                extraction.Adjacencies.Count,
                includeCore,
                capHorizontalOpenFaces,
                extraction.HorizontalUnallocatedCapCount,
                extraction.HorizontalExteriorCapCount,
                extraction.HorizontalExcludedCoreCapCount,
                extraction.IgnoredUnallocatedContacts,
                extraction.IgnoredExteriorContacts,
                extraction.IgnoredCoreContacts,
                extraction.IgnoredSameProgramContacts);
        }
    }
}

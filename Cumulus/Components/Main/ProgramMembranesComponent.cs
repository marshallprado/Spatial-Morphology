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
    /// Creates individual program-colored membrane meshes from the exact
    /// canonical ProgramInterfaceData records produced by Program Interfaces.
    /// This component is visual/derived geometry only; it never changes the
    /// canonical interface mesh or the AnalysisStack.
    /// </summary>
    public sealed class ProgramMembranesComponent : GH_Component
    {
        private const int CoreProgramIndex = -2;
        private const double TotalProgramMeshGap = 0.1;

        private readonly List<Mesh> _previewMeshes = new List<Mesh>();
        private readonly List<Color> _previewColors = new List<Color>();

        public ProgramMembranesComponent()
            : base(
                "Program Membranes",
                "Membranes",
                "Builds one color-coded membrane mesh per program from the exact " +
                "Program Interfaces data.\n\n" +
                "Program-to-program faces may be separated by a fixed 0.1 model-unit " +
                "gap for clearer Rhino viewport display. Program-to-core interfaces " +
                "and horizontal caps remain unoffset.\n\n" +
                "No smoothing is applied. The canonical Program Interfaces output " +
                "remains the exact source topology for future circulation and " +
                "structural refinement.",
                "Cumulus",
                "3 | Main")
        {
        }

        public override Guid ComponentGuid =>
            new Guid("AF580E6A-399C-42EF-AF7A-246D13DBB091");

        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();

                using var stream = assembly.GetManifestResourceStream(
                    "Cumulus.Resources.ProgMembranes_24.png");

                return stream != null ? new Bitmap(stream) : null!;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "analysis_stack",
                "AS",
                "AnalysisStackData from AnalysisStack. Used to retrieve program " +
                "names, colors, VoxelGrid orientation, and voxel size.",
                GH_ParamAccess.item);

            pManager.AddGenericParameter(
                "interface_data",
                "ID",
                "ProgramInterfaceData tree from Program Interfaces ID.",
                GH_ParamAccess.tree);

            pManager.AddBooleanParameter(
                "separate_program_meshes",
                "G",
                "When True, each program-to-program membrane is offset toward " +
                "its own program volume. The total visual separation between " +
                "opposing membranes is 0.1 Rhino model units.\n\n" +
                "Program-to-core interfaces and horizontal caps remain unoffset.",
                GH_ParamAccess.item,
                true);

            pManager.AddBooleanParameter(
                "include_horizontal_caps",
                "HC",
                "Include top and bottom open-boundary cap faces emitted by " +
                "Program Interfaces.",
                GH_ParamAccess.item,
                true);

            pManager[2].Optional = true;
            pManager[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter(
                "membranes",
                "M",
                "One derived membrane mesh per program, grouped by program index.",
                GH_ParamAccess.tree);

            pManager.AddIntegerParameter(
                "program_indices",
                "PI",
                "Program index parallel to each membrane mesh.",
                GH_ParamAccess.tree);

            pManager.AddTextParameter(
                "program_names",
                "PN",
                "Program name parallel to each membrane mesh.",
                GH_ParamAccess.tree);

            pManager.AddColourParameter(
                "colors",
                "C",
                "Program color parallel to each membrane mesh.",
                GH_ParamAccess.tree);

            pManager.AddNumberParameter(
                "represented_interface_area",
                "A",
                "Total canonical interface area represented by each program membrane.",
                GH_ParamAccess.tree);

            pManager.AddGenericParameter(
                "source_interfaces",
                "SI",
                "ProgramInterfaceData records used to create each program membrane.",
                GH_ParamAccess.tree);

            pManager.AddTextParameter(
                "report",
                "R",
                "Membrane creation summary.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            ClearPreviewGeometry();

            object? stackObject = null;
            var interfaceTree = new GH_Structure<IGH_Goo>();
            bool separateProgramMeshes = true;
            bool includeHorizontalCaps = true;

            if (!DA.GetData(0, ref stackObject))
                return;

            if (!DA.GetDataTree(1, out interfaceTree))
                return;

            DA.GetData(2, ref separateProgramMeshes);
            DA.GetData(3, ref includeHorizontalCaps);

            AnalysisStackData? stack = UnwrapStack(stackObject);

            if (stack == null)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Could not read AnalysisStackData from AS.");

                return;
            }

            if (stack.VoxelGrid == null || stack.Programs == null)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "AnalysisStackData is missing VoxelGrid or Programs data.");

                return;
            }

            var interfaceRecords = new List<ProgramInterfaceData>();

            foreach (List<IGH_Goo> branch in interfaceTree.Branches)
            {
                foreach (IGH_Goo goo in branch)
                {
                    ProgramInterfaceData? record = UnwrapInterfaceData(goo);

                    if (record == null)
                    {
                        AddRuntimeMessage(
                            GH_RuntimeMessageLevel.Warning,
                            "Ignored an ID item that is not ProgramInterfaceData.");

                        continue;
                    }

                    interfaceRecords.Add(record);
                }
            }

            if (interfaceRecords.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "No ProgramInterfaceData records were found in ID.");

                return;
            }

            var builders = new Dictionary<int, ProgramMeshBuilder>();

            int skippedCaps = 0;
            int skippedInvalidRecords = 0;
            int programProgramFaceCount = 0;
            int programCoreFaceCount = 0;
            int capFaceCount = 0;

            foreach (ProgramInterfaceData record in interfaceRecords)
            {
                if (record.IsHorizontalOpenBoundaryCap)
                {
                    if (!includeHorizontalCaps)
                    {
                        skippedCaps++;
                        continue;
                    }

                    if (!IsRegularProgram(record.ProgramAIndex, stack.Programs))
                    {
                        skippedInvalidRecords++;
                        continue;
                    }

                    AddFaceToProgram(
                        builders,
                        stack,
                        record.ProgramAIndex,
                        record,
                        Vector3d.Zero);

                    capFaceCount++;
                    continue;
                }

                if (record.IsCoreInterface)
                {
                    int regularProgramIndex = FindRegularProgramSide(
                        record,
                        stack.Programs);

                    if (regularProgramIndex < 0)
                    {
                        skippedInvalidRecords++;
                        continue;
                    }

                    // Core has no visual membrane. The ordinary program receives
                    // the exact canonical face with no gap offset.
                    AddFaceToProgram(
                        builders,
                        stack,
                        regularProgramIndex,
                        record,
                        Vector3d.Zero);

                    programCoreFaceCount++;
                    continue;
                }

                bool validProgramA = IsRegularProgram(
                    record.ProgramAIndex,
                    stack.Programs);

                bool validProgramB = IsRegularProgram(
                    record.ProgramBIndex,
                    stack.Programs);

                if (!validProgramA || !validProgramB ||
                    record.ProgramAIndex == record.ProgramBIndex)
                {
                    skippedInvalidRecords++;
                    continue;
                }

                Vector3d normal = record.Normal;

                if (!normal.Unitize())
                {
                    skippedInvalidRecords++;
                    continue;
                }

                double halfGap = separateProgramMeshes
                    ? TotalProgramMeshGap * 3.0
                    : 0.0;

                // Canonical normal points Program A -> Program B.
                // Each visual membrane moves toward its own program volume.
                Vector3d programAOffset = -normal * halfGap;
                Vector3d programBOffset = normal * halfGap;

                AddFaceToProgram(
                    builders,
                    stack,
                    record.ProgramAIndex,
                    record,
                    programAOffset);

                AddFaceToProgram(
                    builders,
                    stack,
                    record.ProgramBIndex,
                    record,
                    programBOffset);

                programProgramFaceCount++;
            }

            var meshesTree = new GH_Structure<GH_Mesh>();
            var programIndicesTree = new GH_Structure<GH_Integer>();
            var programNamesTree = new GH_Structure<GH_String>();
            var colorsTree = new GH_Structure<GH_Colour>();
            var areasTree = new GH_Structure<GH_Number>();
            var sourceInterfacesTree = new GH_Structure<GH_ObjectWrapper>();

            int outputMeshCount = 0;

            foreach (KeyValuePair<int, ProgramMeshBuilder> pair in builders
                .OrderBy(item => item.Key))
            {
                int programIndex = pair.Key;
                ProgramMeshBuilder builder = pair.Value;

                if (builder.Mesh.Faces.Count == 0)
                    continue;

                builder.Mesh.Normals.ComputeNormals();
                builder.Mesh.Compact();

                var path = new GH_Path(programIndex);

                meshesTree.Append(
                    new GH_Mesh(builder.Mesh),
                    path);

                programIndicesTree.Append(
                    new GH_Integer(programIndex),
                    path);

                programNamesTree.Append(
                    new GH_String(stack.Programs[programIndex].Name),
                    path);

                colorsTree.Append(
                    new GH_Colour(stack.Programs[programIndex].Color),
                    path);

                areasTree.Append(
                    new GH_Number(builder.CanonicalArea),
                    path);

                foreach (ProgramInterfaceData record in builder.SourceRecords)
                {
                    sourceInterfacesTree.Append(
                        new GH_ObjectWrapper(record),
                        path);
                }

                Mesh previewMesh = builder.Mesh.DuplicateMesh();

                if (previewMesh != null)
                {
                    _previewMeshes.Add(previewMesh);
                    _previewColors.Add(stack.Programs[programIndex].Color);
                }

                outputMeshCount++;
            }

            if (outputMeshCount == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "No membrane meshes were created from the supplied interface data.");
            }

            DA.SetDataTree(0, meshesTree);
            DA.SetDataTree(1, programIndicesTree);
            DA.SetDataTree(2, programNamesTree);
            DA.SetDataTree(3, colorsTree);
            DA.SetDataTree(4, areasTree);
            DA.SetDataTree(5, sourceInterfacesTree);
            DA.SetData(6, BuildReport(
                interfaceRecords.Count,
                outputMeshCount,
                programProgramFaceCount,
                programCoreFaceCount,
                capFaceCount,
                skippedCaps,
                skippedInvalidRecords,
                separateProgramMeshes,
                includeHorizontalCaps));
        }

        private static AnalysisStackData? UnwrapStack(object? value)
        {
            if (value is GH_ObjectWrapper wrapper)
                return wrapper.Value as AnalysisStackData;

            return value as AnalysisStackData;
        }

        private static ProgramInterfaceData? UnwrapInterfaceData(IGH_Goo goo)
        {
            if (goo is GH_ObjectWrapper wrapper)
                return wrapper.Value as ProgramInterfaceData;

            return null;
        }

        private static bool IsRegularProgram(
            int programIndex,
            IReadOnlyList<ProgramDefinition> programs)
        {
            return programIndex >= 0 && programIndex < programs.Count;
        }

        private static int FindRegularProgramSide(
            ProgramInterfaceData record,
            IReadOnlyList<ProgramDefinition> programs)
        {
            if (IsRegularProgram(record.ProgramAIndex, programs))
                return record.ProgramAIndex;

            if (IsRegularProgram(record.ProgramBIndex, programs))
                return record.ProgramBIndex;

            return -1;
        }

        private static void AddFaceToProgram(
            Dictionary<int, ProgramMeshBuilder> builders,
            AnalysisStackData stack,
            int programIndex,
            ProgramInterfaceData record,
            Vector3d offset)
        {
            if (!builders.TryGetValue(programIndex, out ProgramMeshBuilder? builder))
            {
                builder = new ProgramMeshBuilder();
                builders.Add(programIndex, builder);
            }

            Point3d[] corners = GetFaceCorners(
                stack.VoxelGrid,
                record,
                offset);

            int start = builder.Mesh.Vertices.Count;

            builder.Mesh.Vertices.Add(corners[0]);
            builder.Mesh.Vertices.Add(corners[1]);
            builder.Mesh.Vertices.Add(corners[2]);
            builder.Mesh.Vertices.Add(corners[3]);
            builder.Mesh.Faces.AddFace(
                start,
                start + 1,
                start + 2,
                start + 3);

            builder.CanonicalArea += record.Area;
            builder.SourceRecords.Add(record);
        }

        private static Point3d[] GetFaceCorners(
            VoxelGrid grid,
            ProgramInterfaceData record,
            Vector3d offset)
        {
            Vector3d tangentU;
            Vector3d tangentV;

            switch (record.GridAxis)
            {
                case 0:
                    tangentU = GridLocalVectorToWorld(grid, Vector3d.YAxis);
                    tangentV = GridLocalVectorToWorld(grid, Vector3d.ZAxis);
                    break;

                case 1:
                    tangentU = GridLocalVectorToWorld(grid, Vector3d.ZAxis);
                    tangentV = GridLocalVectorToWorld(grid, Vector3d.XAxis);
                    break;

                default:
                    tangentU = GridLocalVectorToWorld(grid, Vector3d.XAxis);
                    tangentV = GridLocalVectorToWorld(grid, Vector3d.YAxis);
                    break;
            }

            tangentU.Unitize();
            tangentV.Unitize();

            Vector3d normal = record.Normal;

            if (!normal.Unitize())
                normal = Vector3d.CrossProduct(tangentU, tangentV);

            if (Vector3d.Multiply(
                    Vector3d.CrossProduct(tangentU, tangentV),
                    normal) < 0.0)
            {
                tangentV = -tangentV;
            }

            double halfVoxel = grid.VoxelSize * 0.5;
            Point3d center = record.Center + offset;

            return new[]
            {
                center - tangentU * halfVoxel - tangentV * halfVoxel,
                center + tangentU * halfVoxel - tangentV * halfVoxel,
                center + tangentU * halfVoxel + tangentV * halfVoxel,
                center - tangentU * halfVoxel + tangentV * halfVoxel
            };
        }

        private static Vector3d GridLocalVectorToWorld(
            VoxelGrid grid,
            Vector3d localVector)
        {
            Vector3d worldVector = localVector;
            worldVector.Transform(grid.PlaneToWorld);
            return worldVector;
        }

        private static string BuildReport(
            int sourceRecordCount,
            int outputMeshCount,
            int programProgramFaceCount,
            int programCoreFaceCount,
            int capFaceCount,
            int skippedCaps,
            int skippedInvalidRecords,
            bool separateProgramMeshes,
            bool includeHorizontalCaps)
        {
            string gapDescription = separateProgramMeshes
                ? "program-program gap=0.1 model units"
                : "program-program gap=off";

            return string.Format(
                "Program Membranes | source_interfaces={0} | " +
                "program_meshes={1} | program-program_faces={2} | " +
                "program-core_faces={3} | horizontal_caps={4}\n" +
                "{5} | include_horizontal_caps={6} | " +
                "skipped_caps={7} | skipped_invalid={8}\n" +
                "No smoothing is applied. Program-to-core interfaces and " +
                "horizontal caps remain unoffset.",
                sourceRecordCount,
                outputMeshCount,
                programProgramFaceCount,
                programCoreFaceCount,
                capFaceCount,
                gapDescription,
                includeHorizontalCaps,
                skippedCaps,
                skippedInvalidRecords);
        }

        private void ClearPreviewGeometry()
        {
            foreach (Mesh mesh in _previewMeshes)
                mesh.Dispose();

            _previewMeshes.Clear();
            _previewColors.Clear();
        }

        public override bool IsPreviewCapable => true;

        public override BoundingBox ClippingBox
        {
            get
            {
                var boundingBox = BoundingBox.Empty;

                foreach (Mesh mesh in _previewMeshes)
                {
                    if (mesh != null && mesh.IsValid)
                        boundingBox.Union(mesh.GetBoundingBox(true));
                }

                return boundingBox;
            }
        }

        public override void DrawViewportMeshes(IGH_PreviewArgs args)
        {
            if (Hidden || !IsPreviewCapable)
                return;

            for (int index = 0;
                 index < _previewMeshes.Count &&
                 index < _previewColors.Count;
                 index++)
            {
                Mesh mesh = _previewMeshes[index];

                if (mesh == null || !mesh.IsValid)
                    continue;

                args.Display.DrawMeshShaded(
                    mesh,
                    new Rhino.Display.DisplayMaterial(
                        _previewColors[index]));
            }
        }

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            if (Hidden || !IsPreviewCapable)
                return;

            for (int index = 0;
                 index < _previewMeshes.Count &&
                 index < _previewColors.Count;
                 index++)
            {
                Mesh mesh = _previewMeshes[index];

                if (mesh == null || !mesh.IsValid)
                    continue;

                Color color = _previewColors[index];

                Color wireColor = Color.FromArgb(
                    255,
                    Math.Max(0, color.R - 50),
                    Math.Max(0, color.G - 50),
                    Math.Max(0, color.B - 50));

                args.Display.DrawMeshWires(mesh, wireColor);
            }
        }

        public override void RemovedFromDocument(
            GH_Document document)
        {
            ClearPreviewGeometry();
            base.RemovedFromDocument(document);
        }

        private sealed class ProgramMeshBuilder
        {
            public Mesh Mesh { get; } = new Mesh();

            public double CanonicalArea { get; set; }

            public List<ProgramInterfaceData> SourceRecords { get; } =
                new List<ProgramInterfaceData>();
        }
    }
}
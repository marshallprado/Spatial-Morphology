// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Cumulus.Components.Data;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace Cumulus
{
    /// <summary>
    /// Converts assigned AnalysisStack voxels into connected, editable program
    /// floor-plate regions. The incoming stack is never modified.
    /// </summary>
    public sealed class ProgramFloorPlatesComponent : GH_Component
    {
        private const int CoreProgramIndex = -2;

        private readonly List<Brep> _previewBreps = new List<Brep>();
        private readonly List<Color> _previewColors = new List<Color>();

        public ProgramFloorPlatesComponent()
            : base(
                "Program Floor Plates",
                "FloorPlates",
                "Extracts face-connected program footprints by architectural floor.\n\n" +
                "Each output branch represents one program-floor-region and preserves " +
                "the source voxel indices needed for later architectural evaluation.\n\n" +
                "This component does not fill gaps, reserve circulation, validate floor " +
                "height, or modify the incoming Analysis Stack.",
                "Cumulus",
                "3 | Main")
        {
        }

        public override Guid ComponentGuid =>
            new Guid("3D2CFB64-52C5-4B36-9A9B-84B92E384F4A");

        protected override Bitmap Icon => null;

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "analysis_stack",
                "AS",
                "AnalysisStackData from AnalysisStack.",
                GH_ParamAccess.item);

            pManager.AddIntegerParameter(
                "floor_indices",
                "F",
                "One floor index per AnalysisStack voxel, aligned with " +
                "VoxelGrid.FilledKeys. Connect Floor Index F.",
                GH_ParamAccess.list);

            pManager.AddBooleanParameter(
                "include_core",
                "IC",
                "Include core voxels as separate floor-plate regions.",
                GH_ParamAccess.item,
                true);

            pManager.AddBooleanParameter(
                "include_structure",
                "IS",
                "Include the regular program named 'Structure'. " +
                "Structure is excluded by default.",
                GH_ParamAccess.item,
                false);

            pManager[2].Optional = true;
            pManager[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddBrepParameter(
                "floor_plates",
                "P",
                "Planar Breps for each program-floor-region.",
                GH_ParamAccess.tree);

            pManager.AddCurveParameter(
                "boundaries",
                "B",
                "Closed editable boundary curves. A region with holes has multiple curves.",
                GH_ParamAccess.tree);

            pManager.AddIntegerParameter(
                "program_indices",
                "PI",
                "Program index for each region. Core regions use -2.",
                GH_ParamAccess.tree);

            pManager.AddTextParameter(
                "program_names",
                "PN",
                "Program name for each region. Core regions are named Core.",
                GH_ParamAccess.tree);

            pManager.AddColourParameter(
                "colors",
                "C",
                "Program display colors parallel to the floor-plate region branches.",
                GH_ParamAccess.tree);

            pManager.AddIntegerParameter(
                "floor_indices",
                "FI",
                "Floor index for each region.",
                GH_ParamAccess.tree);

            pManager.AddIntegerParameter(
                "source_voxels",
                "VI",
                "Original AnalysisStack voxel indices represented by each region.",
                GH_ParamAccess.tree);

            pManager.AddNumberParameter(
                "areas",
                "A",
                "Plan area of each region in square Rhino model units.",
                GH_ParamAccess.tree);

            pManager.AddTextParameter(
                "report",
                "R",
                "Extraction summary.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            ClearPreviewGeometry();

            object? stackObject = null;
            var floorIndices = new List<int>();
            bool includeCore = true;
            bool includeStructure = false;

            if (!DA.GetData(0, ref stackObject))
                return;

            if (!DA.GetDataList(1, floorIndices))
                return;

            DA.GetData(2, ref includeCore);
            DA.GetData(3, ref includeStructure);

            AnalysisStackData? stack = UnwrapStack(stackObject);

            if (stack == null)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Could not read AnalysisStackData from AS.");

                return;
            }

            int voxelCount = stack.VoxelGrid.FilledKeys.Count;

            if (stack.ProgramIndices == null ||
                stack.ProgramIndices.Count != voxelCount)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "AnalysisStackData ProgramIndices are not aligned with " +
                    "VoxelGrid.FilledKeys.");

                return;
            }

            if (floorIndices.Count != voxelCount)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    $"F must contain exactly {voxelCount} floor indices, but " +
                    $"received {floorIndices.Count}.");

                return;
            }

            int structureProgramIndex = FindStructureProgramIndex(stack.Programs);

            var keys = stack.VoxelGrid.FilledKeys
                .Select(key => new FloorPlateVoxelKey(
                    key.Item1,
                    key.Item2,
                    key.Item3))
                .ToList();

            FloorPlateExtractionResult extraction;

            try
            {
                extraction = FloorPlateExtractionEngine.Run(
                    new FloorPlateExtractionRequest(
                        keys,
                        stack.ProgramIndices,
                        floorIndices,
                        includeCore,
                        structureProgramIndex,
                        includeStructure));
            }
            catch (Exception exception)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Floor-plate extraction failed: " + exception.Message);

                return;
            }

            var plates = new GH_Structure<GH_Brep>();
            var boundaries = new GH_Structure<GH_Curve>();
            var programIndicesTree = new GH_Structure<GH_Integer>();
            var programNamesTree = new GH_Structure<GH_String>();
            var colorsTree = new GH_Structure<GH_Colour>();
            var floorIndicesTree = new GH_Structure<GH_Integer>();
            var sourceVoxelsTree = new GH_Structure<GH_Integer>();
            var areasTree = new GH_Structure<GH_Number>();

            var regionIndicesByProgramFloor =
                new Dictionary<Tuple<int, int>, int>();

            for (int extractionIndex = 0;
                 extractionIndex < extraction.Regions.Count;
                 extractionIndex++)
            {
                FloorPlateRegion region = extraction.Regions[extractionIndex];

                var programFloorKey = Tuple.Create(
                    region.ProgramIndex,
                    region.FloorIndex);

                int regionWithinFloor = 0;

                if (regionIndicesByProgramFloor.TryGetValue(
                    programFloorKey,
                    out int nextRegionIndex))
                {
                    regionWithinFloor = nextRegionIndex;
                }

                regionIndicesByProgramFloor[programFloorKey] =
                    regionWithinFloor + 1;

                var path = new GH_Path(
                    region.ProgramIndex,
                    region.FloorIndex,
                    regionWithinFloor);

                List<Curve> loops = CreateBoundaryLoops(
                    stack.VoxelGrid,
                    region);

                if (loops.Count == 0)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        $"Could not create a boundary for program " +
                        $"{region.ProgramIndex}, floor {region.FloorIndex}, " +
                        $"region {extractionIndex}.");

                    continue;
                }

                Brep[]? regionBreps = Brep.CreatePlanarBreps(loops);

                if (regionBreps == null || regionBreps.Length == 0)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        $"Could not create a planar floor plate for program " +
                        $"{region.ProgramIndex}, floor {region.FloorIndex}, " +
                        $"region {extractionIndex}.");

                    continue;
                }

                Color programColor = GetProgramColor(
                    stack.Programs,
                    region.ProgramIndex);

                foreach (Brep brep in regionBreps)
                {
                    plates.Append(new GH_Brep(brep), path);

                    Brep previewBrep = brep.DuplicateBrep();

                    if (previewBrep != null)
                    {
                        _previewBreps.Add(previewBrep);
                        _previewColors.Add(programColor);
                    }
                }

                foreach (Curve loop in loops)
                    boundaries.Append(new GH_Curve(loop), path);

                programIndicesTree.Append(
                    new GH_Integer(region.ProgramIndex),
                    path);

                programNamesTree.Append(
                    new GH_String(
                        GetProgramName(stack.Programs, region.ProgramIndex)),
                    path);

                colorsTree.Append(
                    new GH_Colour(programColor),
                    path);

                floorIndicesTree.Append(
                    new GH_Integer(region.FloorIndex),
                    path);

                foreach (int voxelIndex in region.SourceVoxelIndices)
                {
                    sourceVoxelsTree.Append(
                        new GH_Integer(voxelIndex),
                        path);
                }

                double area =
                    region.FootprintCells.Count *
                    stack.VoxelGrid.VoxelSize *
                    stack.VoxelGrid.VoxelSize;

                areasTree.Append(new GH_Number(area), path);
            }

            if (extraction.Regions.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "No allocated program/core/Structure regions matched the " +
                    "inclusion settings.");
            }

            DA.SetDataTree(0, plates);
            DA.SetDataTree(1, boundaries);
            DA.SetDataTree(2, programIndicesTree);
            DA.SetDataTree(3, programNamesTree);
            DA.SetDataTree(4, colorsTree);
            DA.SetDataTree(5, floorIndicesTree);
            DA.SetDataTree(6, sourceVoxelsTree);
            DA.SetDataTree(7, areasTree);
            DA.SetData(8, BuildReport(
                extraction,
                includeCore,
                includeStructure));
        }

        private static AnalysisStackData? UnwrapStack(object? value)
        {
            if (value is GH_ObjectWrapper wrapper)
                return wrapper.Value as AnalysisStackData;

            return value as AnalysisStackData;
        }

        private static int FindStructureProgramIndex(
            IReadOnlyList<ProgramDefinition> programs)
        {
            for (int index = 0; index < programs.Count; index++)
            {
                if (string.Equals(
                    programs[index].Name,
                    "Structure",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return -1;
        }

        private static string GetProgramName(
            IReadOnlyList<ProgramDefinition> programs,
            int programIndex)
        {
            if (programIndex == CoreProgramIndex)
                return "Core";

            return programIndex >= 0 && programIndex < programs.Count
                ? programs[programIndex].Name
                : $"Program {programIndex}";
        }

        private static Color GetProgramColor(
            IReadOnlyList<ProgramDefinition> programs,
            int programIndex)
        {
            if (programIndex == CoreProgramIndex)
                return Color.FromArgb(255, 80, 80, 80);

            if (programIndex >= 0 && programIndex < programs.Count)
                return programs[programIndex].Color;

            return Color.Gray;
        }

        private static string BuildReport(
            FloorPlateExtractionResult result,
            bool includeCore,
            bool includeStructure)
        {
            var lines = new System.Text.StringBuilder();

            lines.AppendLine(
                $"Program Floor Plates | regions={result.Regions.Count} | " +
                $"include_core={includeCore} | " +
                $"include_structure={includeStructure}");

            foreach (var group in result.Regions
                .GroupBy(region => region.ProgramIndex)
                .OrderBy(group => group.Key))
            {
                int footprintCellCount = group.Sum(
                    region => region.FootprintCells.Count);

                int sourceVoxelCount = group.Sum(
                    region => region.SourceVoxelIndices.Count);

                lines.AppendLine(
                    $"  program_index={group.Key} | " +
                    $"regions={group.Count()} | " +
                    $"footprint_cells={footprintCellCount} | " +
                    $"source_voxels={sourceVoxelCount}");
            }

            lines.AppendLine(
                $"Skipped: unallocated={result.SkippedUnallocatedVoxelCount}, " +
                $"core={result.SkippedCoreVoxelCount}, " +
                $"structure={result.SkippedStructureVoxelCount}");

            return lines.ToString().TrimEnd();
        }

        private void ClearPreviewGeometry()
        {
            foreach (Brep brep in _previewBreps)
                brep.Dispose();

            _previewBreps.Clear();
            _previewColors.Clear();
        }

        public override bool IsPreviewCapable => true;

        public override BoundingBox ClippingBox
        {
            get
            {
                var boundingBox = BoundingBox.Empty;

                foreach (Brep brep in _previewBreps)
                {
                    if (brep != null && brep.IsValid)
                        boundingBox.Union(brep.GetBoundingBox(true));
                }

                return boundingBox;
            }
        }

        public override void DrawViewportMeshes(IGH_PreviewArgs args)
        {
            if (Hidden || !IsPreviewCapable)
                return;

            for (int index = 0;
                 index < _previewBreps.Count &&
                 index < _previewColors.Count;
                 index++)
            {
                Brep brep = _previewBreps[index];

                if (brep == null || !brep.IsValid)
                    continue;

                args.Display.DrawBrepShaded(
                    brep,
                    new Rhino.Display.DisplayMaterial(
                        _previewColors[index]));
            }
        }

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            if (Hidden || !IsPreviewCapable)
                return;

            for (int index = 0;
                 index < _previewBreps.Count &&
                 index < _previewColors.Count;
                 index++)
            {
                Brep brep = _previewBreps[index];

                if (brep == null || !brep.IsValid)
                    continue;

                Color color = _previewColors[index];

                Color edgeColor = Color.FromArgb(
                    255,
                    Math.Max(0, color.R - 50),
                    Math.Max(0, color.G - 50),
                    Math.Max(0, color.B - 50));

                args.Display.DrawBrepWires(brep, edgeColor);
            }
        }

        public override void RemovedFromDocument(
            Grasshopper.Kernel.GH_Document document)
        {
            ClearPreviewGeometry();
            base.RemovedFromDocument(document);
        }

        private static List<Curve> CreateBoundaryLoops(
            VoxelGrid grid,
            FloorPlateRegion region)
        {
            var cells = new HashSet<FloorPlateCell>(
                region.FootprintCells);

            // A vertex may have multiple outgoing edges in concave or
            // point-touching voxel conditions. Preserve all edges rather
            // than using a single-value dictionary keyed by vertex.
            var unusedEdges =
                new List<Tuple<PlanVertex, PlanVertex>>();

            foreach (FloorPlateCell cell in cells)
            {
                int x = cell.X;
                int y = cell.Y;

                // Every edge is oriented counter-clockwise, retaining
                // occupied cells on the left side of the boundary.
                if (!cells.Contains(new FloorPlateCell(x, y - 1)))
                {
                    unusedEdges.Add(Tuple.Create(
                        new PlanVertex(x, y),
                        new PlanVertex(x + 1, y)));
                }

                if (!cells.Contains(new FloorPlateCell(x + 1, y)))
                {
                    unusedEdges.Add(Tuple.Create(
                        new PlanVertex(x + 1, y),
                        new PlanVertex(x + 1, y + 1)));
                }

                if (!cells.Contains(new FloorPlateCell(x, y + 1)))
                {
                    unusedEdges.Add(Tuple.Create(
                        new PlanVertex(x + 1, y + 1),
                        new PlanVertex(x, y + 1)));
                }

                if (!cells.Contains(new FloorPlateCell(x - 1, y)))
                {
                    unusedEdges.Add(Tuple.Create(
                        new PlanVertex(x, y + 1),
                        new PlanVertex(x, y)));
                }
            }

            var loops = new List<Curve>();

            while (unusedEdges.Count > 0)
            {
                int firstIndex = unusedEdges
                    .Select((edge, index) => new { edge, index })
                    .OrderBy(item => item.edge.Item1)
                    .ThenBy(item => item.edge.Item2)
                    .First()
                    .index;

                Tuple<PlanVertex, PlanVertex> firstEdge =
                    unusedEdges[firstIndex];

                unusedEdges.RemoveAt(firstIndex);

                PlanVertex start = firstEdge.Item1;
                PlanVertex previous = firstEdge.Item1;
                PlanVertex current = firstEdge.Item2;

                var vertices = new List<PlanVertex>
                {
                    start,
                    current
                };

                int guard = unusedEdges.Count + 4;

                while (!current.Equals(start) && guard-- > 0)
                {
                    var candidateIndices = new List<int>();

                    for (int index = 0;
                         index < unusedEdges.Count;
                         index++)
                    {
                        if (unusedEdges[index].Item1.Equals(current))
                            candidateIndices.Add(index);
                    }

                    if (candidateIndices.Count == 0)
                        break;

                    int nextIndex = ChooseNextBoundaryEdge(
                        previous,
                        current,
                        unusedEdges,
                        candidateIndices);

                    Tuple<PlanVertex, PlanVertex> nextEdge =
                        unusedEdges[nextIndex];

                    unusedEdges.RemoveAt(nextIndex);

                    previous = current;
                    current = nextEdge.Item2;
                    vertices.Add(current);
                }

                // At least three distinct corners plus the closing corner.
                if (!current.Equals(start) || vertices.Count < 4)
                    continue;

                var worldPoints = new List<Point3d>(vertices.Count);

                foreach (PlanVertex vertex in vertices)
                {
                    var localPoint = new Point3d(
                        grid.Origin.X + vertex.X * grid.VoxelSize,
                        grid.Origin.Y + vertex.Y * grid.VoxelSize,
                        grid.Origin.Z +
                            region.MinimumVoxelZ * grid.VoxelSize);

                    localPoint.Transform(grid.PlaneToWorld);
                    worldPoints.Add(localPoint);
                }

                var polyline = new Polyline(worldPoints);

                if (polyline.IsValid && polyline.IsClosed)
                    loops.Add(polyline.ToNurbsCurve());
            }

            return loops;
        }

        private static int ChooseNextBoundaryEdge(
            PlanVertex previous,
            PlanVertex current,
            IReadOnlyList<Tuple<PlanVertex, PlanVertex>> edges,
            IReadOnlyList<int> candidateIndices)
        {
            int incomingX = current.X - previous.X;
            int incomingY = current.Y - previous.Y;

            int bestIndex = candidateIndices[0];
            int bestTurnRank = int.MaxValue;
            PlanVertex bestEnd = edges[bestIndex].Item2;

            foreach (int candidateIndex in candidateIndices)
            {
                PlanVertex candidateEnd = edges[candidateIndex].Item2;

                int outgoingX = candidateEnd.X - current.X;
                int outgoingY = candidateEnd.Y - current.Y;

                int dot = incomingX * outgoingX + incomingY * outgoingY;
                int cross =
                    incomingX * outgoingY -
                    incomingY * outgoingX;

                int turnRank;

                // Prefer left turn, then straight, then right, then reverse.
                if (cross > 0)
                    turnRank = 0;
                else if (dot > 0)
                    turnRank = 1;
                else if (cross < 0)
                    turnRank = 2;
                else
                    turnRank = 3;

                if (turnRank < bestTurnRank ||
                    (turnRank == bestTurnRank &&
                     candidateEnd.CompareTo(bestEnd) < 0))
                {
                    bestIndex = candidateIndex;
                    bestTurnRank = turnRank;
                    bestEnd = candidateEnd;
                }
            }

            return bestIndex;
        }

        private readonly struct PlanVertex :
            IEquatable<PlanVertex>,
            IComparable<PlanVertex>
        {
            public int X { get; }
            public int Y { get; }

            public PlanVertex(int x, int y)
            {
                X = x;
                Y = y;
            }

            public bool Equals(PlanVertex other)
            {
                return X == other.X && Y == other.Y;
            }

            public override bool Equals(object? obj)
            {
                return obj is PlanVertex other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (X * 397) ^ Y;
                }
            }

            public int CompareTo(PlanVertex other)
            {
                int compareX = X.CompareTo(other.X);

                return compareX != 0
                    ? compareX
                    : Y.CompareTo(other.Y);
            }
        }
    }
}
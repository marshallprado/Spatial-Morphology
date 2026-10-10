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
    /// Creates an architectural, topology-like support network in unallocated
    /// AnalysisStack voxels. This is not a structural-engineering calculation.
    /// </summary>
    public sealed class OptimizeStructureComponent : GH_Component
    {
        private static readonly Color StructureColor = Color.FromArgb(255, 82, 87, 94);

        public OptimizeStructureComponent()
            : base(
                "Optimize Structure",
                "OptStruct",
                "Retains unallocated AnalysisStack voxels as a connected, " +
                "architectural support network. Program and core voxels transmit " +
                "conceptual gravity load; the VoxelGrid construction plane is ground.\n\n" +
                "This is a topology-like architectural design tool, not a " +
                "structural-engineering or finite-element calculation.",
                "Cumulus",
                "3 | Main")
        {
        }

        public override Guid ComponentGuid =>
            new Guid("7F5D27A6-9B14-4E55-A583-6C2A67E543C8");

        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();

                using var stream = assembly.GetManifestResourceStream(
                    "Cumulus.Resources.Structure_24.png");

                return stream != null ? new Bitmap(stream) : null!;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "analysis_stack", "AS",
                "AnalysisStackData from AnalysisStack.",
                GH_ParamAccess.item);

            pManager.AddNumberParameter(
                "retained_structure_fraction", "VF",
                "Maximum fraction of initially unallocated voxels retained as generated Structure. " +
                "Required support paths may exceed this target rather than being disconnected.",
                GH_ParamAccess.item,
                0.25);

            pManager.AddIntegerParameter(
                "maximum_iterations", "I",
                "Reserved routing iteration budget. Default is 75.",
                GH_ParamAccess.item,
                75);

            pManager.AddIntegerParameter(
                "max_cantilever_voxels", "MC",
                "Maximum consecutive horizontal voxel steps allowed before a " +
                "downward step is required. Upward support is never permitted.",
                GH_ParamAccess.item,
                4);

            pManager[1].Optional = true;
            pManager[2].Optional = true;
            pManager[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "structured_stack", "SS",
                "New AnalysisStackData with an internally generated Structure program. " +
                "The incoming stack is not changed.",
                GH_ParamAccess.item);

            pManager.AddIntegerParameter(
                "structure_indices", "SI",
                "Unallocated voxel indices retained as Structure.",
                GH_ParamAccess.list);

            pManager.AddGeometryParameter(
                "structure_voxels", "SV",
                "Geometry for retained Structure voxels.",
                GH_ParamAccess.list);

            pManager.AddIntegerParameter(
                "unsupported_indices", "UI",
                "Allocated program or core voxels that do not reach construction-plane ground " +
                "through the final face-connected network.",
                GH_ParamAccess.list);

            pManager.AddCurveParameter(
                "structural_network", "SN",
                "Unique voxel-centre network segments used by the clustered load paths.",
                GH_ParamAccess.list);

            pManager.AddCurveParameter(
                "load_paths", "LP",
                "All valid program-voxel-to-ground polylines, grouped by " +
                "face-connected program cluster. Data-tree branches follow stable " +
                "program/cluster order.",
                GH_ParamAccess.tree);

            pManager.AddNumberParameter(
                "importance", "IM",
                "One structural-importance scalar per VoxelGrid index. " +
                "Program/core and unused candidate voxels have zero importance.",
                GH_ParamAccess.list);

            pManager.AddTextParameter(
                "report", "R",
                "Optimization and grounded-connectivity summary.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            object stackObject = null;
            double retainedFraction = 0.25;
            int iterations = 75;
            int maximumCantilever = 4;

            if (!DA.GetData(0, ref stackObject))
                return;

            DA.GetData(1, ref retainedFraction);
            DA.GetData(2, ref iterations);
            DA.GetData(3, ref maximumCantilever);

            var stack = UnwrapStack(stackObject);
            if (stack == null)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Could not read AnalysisStackData from AS.");
                return;
            }

            if (stack.VoxelGrid == null || stack.ProgramIndices == null ||
                stack.VoxelGrid.FilledKeys.Count != stack.ProgramIndices.Count)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "AnalysisStackData has inconsistent VoxelGrid and ProgramIndices data.");
                return;
            }

            if (stack.Programs.Any(program =>
                string.Equals(program.Name, "Structure", StringComparison.OrdinalIgnoreCase)))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "The incoming AnalysisStack already contains a program named 'Structure'. " +
                    "Rename that program before running Optimize Structure.");
                return;
            }

            retainedFraction = Math.Max(0.0, Math.Min(1.0, retainedFraction));
            iterations = Math.Max(1, iterations);
            maximumCantilever = Math.Max(0, maximumCantilever);

            var keys = stack.VoxelGrid.FilledKeys
                .Select(key => new StructureVoxelKey(key.Item1, key.Item2, key.Item3))
                .ToList();

            var groundIndices = FindConstructionPlaneContacts(stack.VoxelGrid);

            if (groundIndices.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "No filled voxel intersects or lies below the construction plane. " +
                    "No grounded structural network can be created.");
            }

            StructureOptimizationResult result;
            try
            {
                result = StructureOptimizationEngine.Run(
                    new StructureOptimizationRequest(
                        keys,
                        stack.ProgramIndices,
                        groundIndices,
                        retainedFraction,
                        iterations,
                        maximumCantilever));
            }
            catch (Exception exception)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Structure optimization failed: " + exception.Message);
                return;
            }

            if (result.StructureIndices.Count > result.TargetStructureCount &&
                result.TargetStructureCount > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "Required support paths exceed the requested retained structure fraction. " +
                    "The component kept the additional voxels needed for connectivity.");
            }

            if (result.UnsupportedIndices.Count > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    string.Format(
                        "{0} allocated program/core voxel(s) remain without a route to construction-plane ground.",
                        result.UnsupportedIndices.Count));
            }

            var structuredStack = CreateStructuredStack(stack, result.StructureIndices);

            DA.SetData(0, structuredStack);
            DA.SetDataList(1, result.StructureIndices);
            DA.SetDataList(2, CreateVoxelGeometry(stack.VoxelGrid, result.StructureIndices));
            DA.SetDataList(3, result.UnsupportedIndices);
            DA.SetDataList(4, CreateNetworkCurves(stack.VoxelGrid, result.NetworkEdges));
            DA.SetDataTree(5, CreateLoadPathTree(stack.VoxelGrid, result.ClusterPaths));
            DA.SetDataList(6, result.CandidateImportance);
            DA.SetData(7, BuildReport(
                stack, result, retainedFraction, iterations, maximumCantilever));
        }

        private static AnalysisStackData UnwrapStack(object value)
        {
            var wrapper = value as GH_ObjectWrapper;
            return wrapper != null
                ? wrapper.Value as AnalysisStackData
                : value as AnalysisStackData;
        }

        private static List<int> FindConstructionPlaneContacts(VoxelGrid grid)
        {
            var indices = new List<int>();
            double tolerance = Math.Max(1e-9, grid.VoxelSize * 1e-9);

            for (int index = 0; index < grid.FilledKeys.Count; index++)
            {
                var key = grid.FilledKeys[index];

                // VoxelGrid keys are grid-local. The bottom face, not the centre,
                // defines construction-plane contact.
                double localBottomZ = grid.Origin.Z + key.Item3 * grid.VoxelSize;

                if (localBottomZ <= tolerance)
                    indices.Add(index);
            }

            return indices;
        }

        private static AnalysisStackData CreateStructuredStack(
            AnalysisStackData source, IReadOnlyList<int> structureIndices)
        {
            var programs = new List<ProgramDefinition>(source.Programs)
            {
                new ProgramDefinition("Structure", StructureColor, structureIndices.Count)
            };

            int structureProgramIndex = programs.Count - 1;
            var programIndices = new List<int>(source.ProgramIndices);
            foreach (int index in structureIndices)
                programIndices[index] = structureProgramIndex;

            var ranked = source.Ranked != null
                ? source.Ranked.Select(branch => new List<int>(branch)).ToList()
                : new List<List<int>>();

            ranked.Add(structureIndices.OrderBy(index => index).ToList());

            return new AnalysisStackData(
                source.VoxelGrid,
                new List<string>(source.Labels),
                new Dictionary<string, List<double>>(
                    source.Channels.ToDictionary(
                        pair => pair.Key,
                        pair => new List<double>(pair.Value))),
                new Dictionary<string, List<double>>(
                    source.Raw.ToDictionary(
                        pair => pair.Key,
                        pair => new List<double>(pair.Value))),
                programs,
                programIndices,
                new List<double>(source.WinningScore),
                ranked);
        }

        private static List<GeometryBase> CreateVoxelGeometry(
            VoxelGrid grid, IReadOnlyList<int> indices)
        {
            var geometry = new List<GeometryBase>(indices.Count);
            foreach (int index in indices)
                geometry.Add(grid.KeyToGeometry(grid.FilledKeys[index]));
            return geometry;
        }

        private static List<Curve> CreateNetworkCurves(
            VoxelGrid grid, IReadOnlyList<Tuple<int, int>> edges)
        {
            var curves = new List<Curve>(edges.Count);
            foreach (var edge in edges)
            {
                Point3d from = grid.KeyToCenter(grid.FilledKeys[edge.Item1]);
                Point3d to = grid.KeyToCenter(grid.FilledKeys[edge.Item2]);
                curves.Add(new LineCurve(from, to));
            }
            return curves;
        }

        private static GH_Structure<GH_Curve> CreateLoadPathTree(
            VoxelGrid grid,
            IReadOnlyList<IReadOnlyList<IReadOnlyList<int>>> clusterPaths)
        {
            var tree = new GH_Structure<GH_Curve>();

            for (int clusterIndex = 0; clusterIndex < clusterPaths.Count; clusterIndex++)
            {
                var path = new GH_Path(clusterIndex);
                tree.EnsurePath(path);

                foreach (var voxelPath in clusterPaths[clusterIndex])
                {
                    if (voxelPath.Count < 2)
                        continue;

                    var points = voxelPath
                        .Select(index => grid.KeyToCenter(grid.FilledKeys[index]))
                        .ToList();

                    var simplified = SimplifyVoxelPolyline(points);
                    tree.Append(new GH_Curve(new PolylineCurve(simplified)), path);
                }
            }

            return tree;
        }

        private static Polyline SimplifyVoxelPolyline(IReadOnlyList<Point3d> points)
        {
            var result = new Polyline();
            if (points.Count == 0)
                return result;

            result.Add(points[0]);
            if (points.Count == 1)
                return result;

            for (int i = 1; i < points.Count - 1; i++)
            {
                Vector3d previous = points[i] - points[i - 1];
                Vector3d next = points[i + 1] - points[i];

                if (!previous.Unitize() || !next.Unitize() ||
                    !previous.EpsilonEquals(next, 1e-8))
                    result.Add(points[i]);
            }

            result.Add(points[points.Count - 1]);
            return result;
        }

        private static string BuildReport(
            AnalysisStackData stack,
            StructureOptimizationResult result,
            double retainedFraction,
            int iterations,
            int maximumCantilever)
        {
            int allocated = stack.ProgramIndices.Count(index => index != -1);
            int groundedAllocated = allocated - result.UnsupportedIndices.Count;

            return string.Format(
                "Optimize Structure | allocated={0} | unallocated candidates={1} | " +
                "target fraction={2:P0} ({3}) | retained structure={4} | " +
                "ground contacts={5} | program clusters={6} | " +
                "grounded allocated={7} | unsupported allocated={8} | iteration budget={9} | " +
                "max cantilever={10}\n" +
                "Ground is the VoxelGrid construction plane. Programs attach to the " +
                "nearest valid grounded core/structure network or ground using only " +
                "downward and cantilever-limited horizontal moves. " +
                "This is an architectural connectivity model, not engineering analysis.",
                allocated,
                result.CandidateCount,
                retainedFraction,
                result.TargetStructureCount,
                result.StructureIndices.Count,
                result.GroundContactCount,
                result.ProgramClusters.Count,
                groundedAllocated,
                result.UnsupportedIndices.Count,
                iterations,
                maximumCantilever);
        }
    }
}

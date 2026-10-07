// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace Cumulus
{
    /// <summary>
    /// Adds a dedicated structure program in unassigned voxels so every occupied
    /// program component reaches the ground or a trusted core anchor.
    /// </summary>
    public sealed class GrowSupportsComponent : GH_Component
    {
        public GrowSupportsComponent()
            : base(
                "Grow Supports", "Supports",
                "Audits an AnalysisStack for face-connected floating program volume and " +
                "routes a dedicated Structure program through unassigned voxels to the " +
                "bottom layer or trusted core anchors. Existing program voxels are never replaced.",
                "Cumulus", "4 | Post Process")
        {
        }

        public override Guid ComponentGuid =>
            new Guid("A1F27C93-4F71-4B28-9E47-7B819C1D6F2A");

        protected override Bitmap Icon => null;

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("analysis_stack", "AS",
                "AnalysisStack object from AnalysisStack.", GH_ParamAccess.item);
            pManager.AddGenericParameter("support_value_set", "SVS",
                "Dedicated ValueSet used to rank legal support voxels. Its ProgramName " +
                "becomes the Structure program name.", GH_ParamAccess.item);
            pManager.AddIntegerParameter("core_anchor_indices", "CA",
                "Optional occupied voxel indices that are trusted structural anchors. " +
                "Core voxels already stored by AnalysisStack are anchors automatically.",
                GH_ParamAccess.list);
            pManager.AddColourParameter("support_color", "C",
                "Display color for the dedicated Structure program.",
                GH_ParamAccess.item, Color.FromArgb(255, 90, 90, 90));
            pManager.AddNumberParameter("horizontal_penalty", "HP",
                "Additional cost for each lateral support step. Higher values favour " +
                "more vertical routes. Default: 2.0.", GH_ParamAccess.item, 2.0);
            pManager.AddNumberParameter("analysis_penalty", "AP",
                "Penalty for using a low-scoring support ValueSet cell. 0 ignores " +
                "analysis values; higher values favour better cells. Default: 1.0.",
                GH_ParamAccess.item, 1.0);

            pManager[2].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("supported_stack", "SS",
                "New AnalysisStackData with Structure appended as a dedicated program. " +
                "The input stack is not modified.", GH_ParamAccess.item);
            pManager.AddIntegerParameter("support_indices", "SI",
                "Voxel indices newly assigned to Structure.", GH_ParamAccess.list);
            pManager.AddGeometryParameter("support_voxels", "SV",
                "Geometry of voxels newly assigned to Structure.", GH_ParamAccess.list);
            pManager.AddIntegerParameter("unresolved_indices", "UI",
                "Floating components with no route through unassigned voxels. " +
                "One branch per unresolved component.", GH_ParamAccess.tree);
            pManager.AddTextParameter("report", "R",
                "Support-growth diagnostics.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            object stackObject = null;
            object valueSetObject = null;
            var coreInput = new List<int>();
            Color supportColor = Color.FromArgb(255, 90, 90, 90);
            double horizontalPenalty = 2.0;
            double analysisPenalty = 1.0;

            if (!DA.GetData(0, ref stackObject)) return;
            if (!DA.GetData(1, ref valueSetObject)) return;
            DA.GetDataList(2, coreInput);
            DA.GetData(3, ref supportColor);
            DA.GetData(4, ref horizontalPenalty);
            DA.GetData(5, ref analysisPenalty);

            AnalysisStackData stack = UnwrapStack(stackObject);
            ValueSet supportValueSet = UnwrapValueSet(valueSetObject);

            if (stack == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Could not read AnalysisStackData from 'analysis_stack'.");
                return;
            }
            if (supportValueSet == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Could not read a ValueSet from 'support_value_set'.");
                return;
            }
            if (stack.NVoxels == 0 || stack.ProgramIndices.Count != stack.NVoxels)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "AnalysisStackData contains no valid voxel assignment.");
                return;
            }
            if (horizontalPenalty < 0.0 || analysisPenalty < 0.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "horizontal_penalty and analysis_penalty must be non-negative.");
                return;
            }

            List<double> suitability;
            List<string> unusedLabels;
            try
            {
                suitability = BuildSupportSuitability(stack, supportValueSet, out unusedLabels);
            }
            catch (ArgumentException ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                return;
            }

            if (unusedLabels.Count > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "The support ValueSet refers to channels absent from this stack: " +
                    string.Join(", ", unusedLabels) + ".");

            int supportProgramIndex = stack.Programs.FindIndex(
                p => string.Equals(p.Name, supportValueSet.ProgramName,
                    StringComparison.OrdinalIgnoreCase));

            var programs = new List<ProgramDefinition>(stack.Programs);
            var ranked = stack.Ranked.Select(r => new List<int>(r)).ToList();

            if (supportProgramIndex < 0)
            {
                supportProgramIndex = programs.Count;
                programs.Add(new ProgramDefinition(supportValueSet.ProgramName,
                    supportColor, -1));
                ranked.Add(new List<int>());
            }

            var trustedAnchors = new HashSet<int>();
            for (int i = 0; i < stack.NVoxels; i++)
                if (stack.ProgramIndices[i] == -2)
                    trustedAnchors.Add(i);

            foreach (int index in coreInput.Distinct())
            {
                if (index < 0 || index >= stack.NVoxels)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        "Ignoring out-of-range core anchor index " + index + ".");
                    continue;
                }
                if (stack.ProgramIndices[index] == -1)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        "Ignoring core anchor index " + index +
                        " because it is unassigned. Anchor indices must already be occupied.");
                    continue;
                }
                trustedAnchors.Add(index);
            }

            var keys = stack.VoxelGrid.FilledKeys
                .Select(k => new VoxelKey(k.Item1, k.Item2, k.Item3))
                .ToList();

            SupportGrowthResult result;
            try
            {
                result = SupportGrowthEngine.Run(new SupportGrowthRequest(
                    keys, stack.ProgramIndices, suitability, trustedAnchors,
                    supportProgramIndex, horizontalPenalty, analysisPenalty));
            }
            catch (ArgumentException ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                return;
            }

            foreach (int index in result.AddedSupportIndices)
                ranked[supportProgramIndex].Add(index);

            var winningScores = new List<double>(stack.WinningScore);
            while (winningScores.Count < stack.NVoxels) winningScores.Add(0.0);
            foreach (int index in result.AddedSupportIndices)
                winningScores[index] = suitability[index];

            var outputStack = new AnalysisStackData(
                stack.VoxelGrid,
                new List<string>(stack.Labels),
                stack.Channels,
                stack.Raw,
                programs,
                result.ProgramIndices,
                winningScores,
                ranked);

            var supportGeometry = new List<GeometryBase>();
            foreach (int index in result.AddedSupportIndices)
                supportGeometry.Add(stack.VoxelGrid.KeyToGeometry(
                    stack.VoxelGrid.FilledKeys[index]));

            var unresolvedTree = new GH_Structure<GH_Integer>();
            for (int branch = 0; branch < result.UnresolvedComponents.Count; branch++)
            {
                var path = new GH_Path(branch);
                foreach (int index in result.UnresolvedComponents[branch])
                    unresolvedTree.Append(new GH_Integer(index), path);
            }

            if (result.UnresolvedComponents.Count > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    result.UnresolvedComponents.Count +
                    " floating component(s) could not reach support through unassigned voxels.");

            if (result.FloatingComponentCount > 0 && result.AddedSupportIndices.Count == 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "No legal support route was found. Reserve unassigned voxels in " +
                    "AnalysisStack or reduce program voxel-count caps.");

            DA.SetData(0, outputStack);
            DA.SetDataList(1, result.AddedSupportIndices);
            DA.SetDataList(2, supportGeometry);
            DA.SetDataTree(3, unresolvedTree);
            DA.SetData(4, BuildReport(result, supportValueSet, supportProgramIndex,
                trustedAnchors.Count, horizontalPenalty, analysisPenalty));
        }

        private static List<double> BuildSupportSuitability(AnalysisStackData stack,
            ValueSet valueSet, out List<string> unusedLabels)
        {
            unusedLabels = new List<string>();
            var active = new List<KeyValuePair<string, double>>();

            foreach (var weight in valueSet.Weights)
            {
                if (weight.Value == 0.0) continue;
                if (stack.Channels.ContainsKey(weight.Key))
                    active.Add(weight);
                else
                    unusedLabels.Add(weight.Key);
            }

            if (active.Count == 0)
                throw new ArgumentException(
                    "The support ValueSet needs at least one non-zero weight whose label " +
                    "matches an AnalysisStack channel.");

            double divisor = active.Sum(w => Math.Abs(w.Value));
            var scores = new List<double>(stack.NVoxels);

            for (int voxel = 0; voxel < stack.NVoxels; voxel++)
            {
                double total = 0.0;
                foreach (var channel in active)
                {
                    double value = stack.Channels[channel.Key][voxel];
                    if (double.IsNaN(value) || double.IsInfinity(value)) value = 0.0;
                    value = Math.Max(0.0, Math.Min(1.0, value));
                    if (channel.Value < 0.0) value = 1.0 - value;
                    total += Math.Abs(channel.Value) * value;
                }
                scores.Add(total / divisor);
            }

            return scores;
        }

        private static string BuildReport(SupportGrowthResult result, ValueSet valueSet,
            int structureIndex, int anchorCount, double horizontalPenalty,
            double analysisPenalty)
        {
            return string.Format(
                "Grow Supports\n" +
                "  structure ValueSet : '{0}' (program branch {1})\n" +
                "  floating components : {2}\n" +
                "  resolved components : {3}\n" +
                "  unresolved          : {4}\n" +
                "  support voxels added: {5}\n" +
                "  trusted core anchors: {6}\n" +
                "  horizontal penalty  : {7:F3}\n" +
                "  analysis penalty    : {8:F3}\n" +
                "  routing             : face neighbours only; new support uses unassigned cells only.",
                valueSet.ProgramName, structureIndex,
                result.FloatingComponentCount, result.ResolvedComponentCount,
                result.UnresolvedComponents.Count, result.AddedSupportIndices.Count,
                anchorCount, horizontalPenalty, analysisPenalty);
        }

        private static AnalysisStackData UnwrapStack(object value)
        {
            var inner = value is GH_ObjectWrapper wrapper ? wrapper.Value : value;
            return inner as AnalysisStackData;
        }

        private static ValueSet UnwrapValueSet(object value)
        {
            var inner = value is GH_ObjectWrapper wrapper ? wrapper.Value : value;
            return inner as ValueSet;
        }
    }
}

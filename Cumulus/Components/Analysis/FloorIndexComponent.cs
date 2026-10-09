// -*- coding: utf-8 -*-
using Grasshopper.Kernel;
using System;
using System.Drawing;

namespace Cumulus
{
    /// <summary>
    /// Reports each voxel's discrete floor index relative to the VoxelGrid
    /// construction plane. There is no floor zero.
    /// </summary>
    public sealed class FloorIndexComponent : FloorAnalysisComponentBase
    {
        public FloorIndexComponent()
            : base(
                "Floor Index",
                "FloorIdx",
                "Reports the discrete floor number for every voxel relative to the " +
                "VoxelGrid construction plane.\n\n" +
                "There is no floor 0: floor 1 is the first storey at or above the " +
                "construction plane, and floor -1 is the first storey below it.\n\n" +
                "Use Walkup Value, Commercial Value, or Real Estate Value when you " +
                "need a floor-preference analysis rather than raw storey indices.",
                FloorAnalysisKind.FloorIndex,
                "floor_index")
        {
        }

        public override GH_Exposure Exposure => GH_Exposure.tertiary;

        public override Guid ComponentGuid =>
            new Guid("B0DF004C-0BB6-4B40-8D6B-64EDE2A42043");

        protected override Bitmap Icon => LoadIcon("Cumulus.Resources.FloorLevel_24.png");

        private static Bitmap LoadIcon(string resourceName)
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(resourceName);
            return stream != null ? new Bitmap(stream) : null!;
        }
    }
}

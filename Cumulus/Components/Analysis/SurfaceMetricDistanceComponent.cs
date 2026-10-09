// -*- coding: utf-8 -*-
using System;
using System.Drawing;

namespace Cumulus
{
    /// <summary>
    /// Measures model-unit distance from the VoxelGrid surface shell.
    /// </summary>
    public sealed class SurfaceMetricDistanceComponent : SurfaceShellDistanceComponentBase
    {
        public SurfaceMetricDistanceComponent()
            : base(
                "Surface Distance",
                "SurfDist",
                "Calculates metric distance from each voxel to the VoxelGrid surface shell.\n" +
                "Values are returned in Rhino model units.\n\n" +
                "Use Surface Depth for discrete face-neighbour shell depth.",
                true,
                "surface_distance")
        {
        }

        public override Guid ComponentGuid =>
            new Guid("7B14EC0B-491A-4A77-8A75-0A7E3C62F4A9");

        protected override Bitmap? Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "Cumulus.Resources.SurfaceDistance_24.png");

                return stream != null ? new Bitmap(stream) : null;
            }
        }
    }
}

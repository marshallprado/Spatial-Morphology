// -*- coding: utf-8 -*-
using System;
using System.Drawing;

namespace Cumulus
{
    /// <summary>
    /// Calculates discrete face-neighbour shell depth from the outer surface
    /// of a VoxelGrid. Surface voxels have value zero; each inward shell
    /// increases by one.
    /// </summary>
    public sealed class SurfaceDepthComponent : SurfaceShellDistanceComponentBase
    {
        public SurfaceDepthComponent()
            : base(
                "Surface Depth",
                "SurfDepth",
                "Calculates discrete depth from each voxel to the VoxelGrid surface shell.\n" +
                "Surface voxels have a value of 0; each face-connected inward shell " +
                "increases by 1.\n\n" +
                "Use Surface Distance for metric distance in Rhino model units.",
                false,
                "surface_depth")
        {
        }

        /// <summary>
        /// Do not change this GUID after the component has been released.
        /// </summary>
        public override Guid ComponentGuid =>
            new Guid("8A973D51-586B-4B4A-AB4C-2D6D0A971C2A");

        /// <summary>
        /// Loads the embedded Surface Depth Grasshopper icon.
        /// </summary>
        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();

                using var stream = assembly.GetManifestResourceStream(
                    "Cumulus.Resources.SurfaceDepth_24.png");

                return stream != null ? new Bitmap(stream) : null!;
            }
        }
    }
}
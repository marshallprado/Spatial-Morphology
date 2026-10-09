// -*- coding: utf-8 -*-
using Grasshopper.Kernel;
using System;
using System.Drawing;

namespace Cumulus
{
    /// <summary>
    /// Evaluates spatial openness by casting rays in the VoxelGrid construction plane.
    /// </summary>
    public sealed class HorizonViewComponent : ViewAnalysisComponentBase
    {
        public HorizonViewComponent()
            : base(
                "Horizon View",
                "HorView",
                "Measures horizontal spatial openness around each voxel using rays " +
                "distributed in the VoxelGrid construction plane.\n\n" +
                "Use for room connectivity, street visibility, urban space quality, " +
                "and horizontal spatial openness.\n\n" +
                "Use Sky View for full three-dimensional openness.",
                false,
                "horizon_view",
                72)
        {
        }

        public override GH_Exposure Exposure => GH_Exposure.primary;

        public override Guid ComponentGuid =>
            new Guid("0FA76FD2-6F2C-49A3-AB4E-4B89DBAF8487");

        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();

                using var stream = assembly.GetManifestResourceStream(
                    "Cumulus.Resources.HorizonView_24.png");

                return stream != null ? new Bitmap(stream) : null!;
            }
        }
    }
}
// -*- coding: utf-8 -*-
using System;
using System.Drawing;

namespace Cumulus
{
    /// <summary>
    /// Evaluates spatial openness by casting rays uniformly over a full sphere.
    /// </summary>
    public sealed class SkyViewComponent : ViewAnalysisComponentBase
    {
        public SkyViewComponent()
           : base(
                "Sky View",
                "SkyView",
                "Measures sky and three-dimensional spatial openness around each voxel " +
                "using a full spherical Fibonacci ray distribution.\n\n" +
                "Use for atria, sky exposure, vertical connection, and overall " +
                "three-dimensional openness.\n\n" +
                "Use Horizon View for construction-plane-only spatial openness.",
                true,
                "sky_view",
                200)
        {
        }

        public override Guid ComponentGuid =>
            new Guid("D1E6B2A4-2E7D-4FA1-9460-0E8BCA2D5697");

        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();

                using var stream = assembly.GetManifestResourceStream(
                    "Cumulus.Resources.SkyView_24.png");

                return stream != null ? new Bitmap(stream) : null!;
            }
        }
    }
}
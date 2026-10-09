// -*- coding: utf-8 -*-
using Grasshopper.Kernel;
using System;
using System.Drawing;

namespace Cumulus
{
    /// <summary>
    /// Creates core voxels from user-supplied curve paths and evaluates proximity to them.
    /// </summary>
    public sealed class CoreFromCurvesComponent : CoreLocationComponentBase
    {
        protected override bool IsGenerative => false;

        public CoreFromCurvesComponent()
            : base(
                "Core from Curves",
                "CoreCurves",
                "Creates vertical-circulation core voxels from one or more supplied curves.\n\n" +
                "Each curve defines one independent core. Voxels within R of a curve " +
                "are designated as core.\n\n" +
                "The scalar output defaults to near core = high.")
        {
        }
        public override GH_Exposure Exposure => GH_Exposure.tertiary;
        public override Guid ComponentGuid =>
            new Guid("2B739926-7F0F-4F11-9E56-571D062A505A");

        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream(
                    "Cumulus.Resources.CoreLocation_24.png");
                return stream != null ? new Bitmap(stream) : null!;
            }
        }
    }
}

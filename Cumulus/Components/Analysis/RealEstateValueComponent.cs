// -*- coding: utf-8 -*-
using Grasshopper.Kernel;
using System;
using System.Drawing;

namespace Cumulus
{
    /// <summary>
    /// Evaluates floor desirability with the progressive real-estate and
    /// penthouse value curve used by the legacy Floor Level component.
    /// </summary>
    public sealed class RealEstateValueComponent : FloorAnalysisComponentBase
    {
        public RealEstateValueComponent()
            : base(
                "Real Estate Value",
                "REValue",
                "Evaluates progressive real-estate floor value.\n\n" +
                "Floor 1 establishes the base value. Higher floors compound by the " +
                "existing market-growth schedule, including premium jumps and a " +
                "penthouse band. Below-street floors score 0. Output is normalized " +
                "to 0–1 before optional inversion.",
                FloorAnalysisKind.RealEstate,
                "real_estate_value")
        {
        }

        public override GH_Exposure Exposure => GH_Exposure.tertiary;

        public override Guid ComponentGuid =>
            new Guid("AF94C198-65D5-4A25-8654-B0C0D45236A7");

        protected override Bitmap Icon => LoadIcon("Cumulus.Resources.RealEstate_24.png");

        private static Bitmap LoadIcon(string resourceName)
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(resourceName);
            return stream != null ? new Bitmap(stream) : null!;
        }
    }
}

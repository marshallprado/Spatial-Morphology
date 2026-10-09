// -*- coding: utf-8 -*-
using System;
using System.Drawing;

namespace Cumulus
{
    /// <summary>
    /// Evaluates floor desirability using the existing commercial footfall curve.
    /// </summary>
    public sealed class CommercialValueComponent : FloorAnalysisComponentBase
    {
        public CommercialValueComponent()
            : base(
                "Commercial Value",
                "Commercial",
                "Evaluates retail and commercial floor desirability.\n\n" +
                "Street level scores highest, values step down by one per floor above, " +
                "and floors below street level score 0.",
                FloorAnalysisKind.Commercial,
                "commercial_value")
        {
        }

        public override Guid ComponentGuid =>
            new Guid("6A64F641-24A4-42B4-9B0F-E04E0DCFE8B7");

        protected override Bitmap Icon => LoadIcon("Cumulus.Resources.Commercial_24.png");

        private static Bitmap LoadIcon(string resourceName)
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(resourceName);
            return stream != null ? new Bitmap(stream) : null!;
        }
    }
}

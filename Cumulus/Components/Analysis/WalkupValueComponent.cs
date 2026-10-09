// -*- coding: utf-8 -*-
using System;
using System.Drawing;

namespace Cumulus
{
    /// <summary>
    /// Evaluates floor desirability using the existing symmetric walkup curve.
    /// </summary>
    public sealed class WalkupValueComponent : FloorAnalysisComponentBase
    {
        public WalkupValueComponent()
            : base(
                "Walkup Value",
                "Walkup",
                "Evaluates floor desirability for a building without lift service.\n\n" +
                "Street level scores 5. The value drops by one for each floor of " +
                "travel above or below street level; floors 4–6 away score 1 and " +
                "floors 7 or more away score 0.",
                FloorAnalysisKind.Walkup,
                "walkup_value")
        {
        }

        public override Guid ComponentGuid =>
            new Guid("37DA090B-C1C2-4A70-9F5F-7E2EE145E713");

        protected override Bitmap Icon => LoadIcon("Cumulus.Resources.Walkup_24.png");

        private static Bitmap LoadIcon(string resourceName)
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(resourceName);
            return stream != null ? new Bitmap(stream) : null!;
        }
    }
}

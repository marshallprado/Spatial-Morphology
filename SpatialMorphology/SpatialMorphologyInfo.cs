using System;
using System.Drawing;
using Grasshopper.Kernel;

namespace SpatialMorphology
{
    public class SpatialMorphologyInfo : GH_AssemblyInfo
    {
        public override string Name => "SpatialMorphology";

        public override Bitmap? Icon => null;

        public override string Description =>
            "Grasshopper plugin for voxel-based spatial analysis and programmatic space assignment.";

        public override Guid Id => new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");

        public override string AuthorName => "";

        public override string AuthorContact => "";

        public override string Version => "1.0.0";
    }
}

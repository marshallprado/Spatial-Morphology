// -*- coding: utf-8 -*-
using Grasshopper.Kernel;
using System;
using System.Drawing;

namespace Cumulus
{
    /// <summary>
    /// Generates vertical-circulation core paths with dynamic programming and floor BFS coverage.
    /// </summary>
    public sealed class GenerateCoresComponent : CoreLocationComponentBase
    {
        protected override bool IsGenerative => true;

        public GenerateCoresComponent()
            : base(
                "Generate Cores",
                "GenCores",
                "Generates vertical-circulation core paths using dynamic programming " +
                "and connected floor-travel coverage.\n\n" +
                "Optional analysis channels attract core paths toward higher normalized values. " +
                "Cores are added until MD is satisfied where possible.\n\n" +
                "The scalar output defaults to near core = high.")
        {
        }
        public override GH_Exposure Exposure => GH_Exposure.tertiary;
        public override Guid ComponentGuid =>
            new Guid("37A7B130-31E7-4B3F-8E81-3A044C51CBFB");

        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream(
                    "Cumulus.Resources.GenerateCore_24.png");
                return stream != null ? new Bitmap(stream) : null!;
            }
        }
    }
}

// -*- coding: utf-8 -*-
// Version 2.1.0
using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;

namespace SpatialMorphology
{
    public class NormalizeValuesComponent : GH_Component
    {
        public NormalizeValuesComponent()
            : base(
                "NormalizeValues",
                "Normalize",
                "Remaps a list of numbers to [0, 1] using min-max normalization.\n" +
                "When Reverse is true, values are multiplied by -1 before normalization,\n" +
                "reversing their normalized order.\n\n" +
                "If all values are equal, output is all 0.0.\n\n" +
                "Version 2.1.0",
                "Spatial Morphology",
                "1 | Setup")
        { }

        public override Guid ComponentGuid =>
            new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567891");

        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "SpatialMorphology.Resources.NormalizeValues_24.png");
                return stream != null ? new Bitmap(stream) : null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddNumberParameter("values", "V",
                "List of numbers to normalize.\nSet input access to List in GH.",
                GH_ParamAccess.list);
            pManager.AddBooleanParameter("reverse", "R",
                "When true, multiply values by -1 before normalizing.\n" +
                "This reverses the normalized order while keeping the output range [0, 1].\n" +
                "Default: false.",
                GH_ParamAccess.item, false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddNumberParameter("normalized", "N",
                "Values remapped to [0, 1].",
                GH_ParamAccess.list);
            pManager.AddTextParameter("info", "I",
                "Summary.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var values = new List<double>();
            bool reverse = false;

            if (!DA.GetDataList(0, values)) return;
            DA.GetData(1, ref reverse);

            if (values.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No values provided.");
                return;
            }

            var clean = new List<double>(values.Count);
            int nInvalid = 0;

            foreach (var value in values)
            {
                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    clean.Add(0.0);
                    nInvalid++;
                }
                else
                {
                    clean.Add(reverse ? -value : value);
                }
            }

            double lo = clean[0];
            double hi = clean[0];

            foreach (var value in clean)
            {
                if (value < lo) lo = value;
                if (value > hi) hi = value;
            }

            var normalized = new List<double>(clean.Count);
            double span = hi - lo;

            if (Math.Abs(span) < 1e-12)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "All values are equal — output is all 0.0.");

                for (int i = 0; i < clean.Count; i++)
                    normalized.Add(0.0);
            }
            else
            {
                foreach (var value in clean)
                    normalized.Add((value - lo) / span);
            }

            double normMin = normalized[0];
            double normMax = normalized[0];

            foreach (var value in normalized)
            {
                if (value < normMin) normMin = value;
                if (value > normMax) normMax = value;
            }

            string info = string.Format(
                "NormalizeValues | count={0} | processed=[{1:F4} to {2:F4}] | " +
                "reverse={3} | output=[{4:F4} to {5:F4}]{6}",
                normalized.Count,
                lo, hi,
                reverse,
                normMin, normMax,
                nInvalid > 0
                    ? string.Format(" | WARNING: {0} invalid values replaced with 0.0", nInvalid)
                    : "");

            DA.SetDataList(0, normalized);
            DA.SetData(1, info);
        }
    }
}
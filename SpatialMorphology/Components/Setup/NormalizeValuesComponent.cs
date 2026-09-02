// -*- coding: utf-8 -*-
// Version 2.0.0
using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;

namespace SpatialMorphology
{
    public class NormalizeValuesComponent : GH_Component
    {
        // ── Constructor ───────────────────────────────────────────────────────
        public NormalizeValuesComponent()
            : base(
                "NormalizeValues",
                "Normalize",
                "Remaps a list of numbers to [0, 1] using min-max normalization,\n" +
                "then multiplies by a scalar multiplier.\n\n" +
                "  normalized[i] = ((value[i] - min) / (max - min)) * multiplier\n\n" +
                "If all values are equal, output is all 0.0.\n\n" +
                "Version 2.0.0",
                "Spatial Morphology",
                "Setup")
        { }

        // ── GUID ──────────────────────────────────────────────────────────────
        public override Guid ComponentGuid =>
            new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567891");

        // ── Icon ──────────────────────────────────────────────────────────────
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

        // ── Parameters ────────────────────────────────────────────────────────
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddNumberParameter("values", "V",
                "List of numbers to normalize.\n" +
                "Set input access to List in GH.",
                GH_ParamAccess.list);
            pManager.AddNumberParameter("multiplier", "M",
                "Scalar multiplier applied after normalization.\n" +
                "Default: 1.0  →  output range [0, 1]\n" +
                "Example: 100  →  output range [0, 100]\n" +
                "Example: -1   →  output range [-1, 0] (inverts the values)",
                GH_ParamAccess.item, 1.0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddNumberParameter("normalized", "N",
                "Values remapped to [0, multiplier].",
                GH_ParamAccess.list);
            pManager.AddTextParameter("info", "I",
                "Summary.",
                GH_ParamAccess.item);
        }

        // ── Solve ─────────────────────────────────────────────────────────────
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // ── Collect inputs ────────────────────────────────────────────────
            var values = new List<double>();
            double multiplier = 1.0;

            if (!DA.GetDataList(0, values)) return;
            DA.GetData(1, ref multiplier);

            if (values.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "No values provided.");
                return;
            }

            // ── Sanitize — replace NaN and infinity with 0.0 ──────────────────
            var clean = new List<double>(values.Count);
            int nInvalid = 0;

            foreach (var v in values)
            {
                if (double.IsNaN(v) || double.IsInfinity(v))
                {
                    clean.Add(0.0);
                    nInvalid++;
                }
                else
                {
                    clean.Add(v);
                }
            }

            // ── Min-max normalize ─────────────────────────────────────────────
            double lo = clean[0];
            double hi = clean[0];

            foreach (var v in clean)
            {
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }

            var normalized = new List<double>(clean.Count);
            double span = hi - lo;

            if (Math.Abs(span) < 1e-12)
            {
                // Flat input — all values equal → all 0.0
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "All values are equal — output is all 0.0.");
                for (int i = 0; i < clean.Count; i++)
                    normalized.Add(0.0);
            }
            else
            {
                foreach (var v in clean)
                    normalized.Add(((v - lo) / span) * multiplier);
            }

            // ── Info ──────────────────────────────────────────────────────────
            double normMin = normalized.Count > 0 ? normalized[0] : 0;
            double normMax = normalized.Count > 0 ? normalized[0] : 0;

            foreach (var v in normalized)
            {
                if (v < normMin) normMin = v;
                if (v > normMax) normMax = v;
            }

            string info = string.Format(
                "NormalizeValues | count={0} | " +
                "raw=[{1:F4} to {2:F4}] | " +
                "multiplier={3:F4} | " +
                "output=[{4:F4} to {5:F4}]{6}",
                normalized.Count,
                lo, hi,
                multiplier,
                normMin, normMax,
                nInvalid > 0
                    ? string.Format(
                        " | WARNING: {0} invalid values replaced with 0.0",
                        nInvalid)
                    : "");

            // ── Output ────────────────────────────────────────────────────────
            DA.SetDataList(0, normalized);
            DA.SetData(1, info);
        }
    }
}
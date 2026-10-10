// -*- coding: utf-8 -*-
// Version 1.2.0
using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace Cumulus
{
    /// <summary>
    /// Shared base class for all SA components.
    /// </summary>
    public abstract class SAComponentBase : GH_Component
    {
        protected List<Point3d> _previewPoints = new List<Point3d>();
        protected List<Color> _previewColors = new List<Color>();

        protected SAComponentBase(
            string name, string nickname, string description,
            string category, string subcategory)
            : base(name, nickname, description, category, subcategory)
        { }

        // Low = red, high = blue.
        protected static readonly Color[] GRADIENT_STOPS = new Color[]
        {
            Color.FromArgb(255, 234,  38,   0),
            Color.FromArgb(255, 234, 126,   0),
            Color.FromArgb(255, 254, 244,  84),
            Color.FromArgb(255, 173, 203, 249),
            Color.FromArgb(255,  75, 107, 169),
        };

        protected static Color InterpolateGradient(double t)
        {
            t = Math.Max(0.0, Math.Min(1.0, t));
            double scaled = t * (GRADIENT_STOPS.Length - 1);
            int idxLo = (int)Math.Floor(scaled);
            int idxHi = Math.Min(idxLo + 1, GRADIENT_STOPS.Length - 1);
            double frac = scaled - idxLo;

            Color lo = GRADIENT_STOPS[idxLo];
            Color hi = GRADIENT_STOPS[idxHi];

            return Color.FromArgb(255,
                Math.Max(0, Math.Min(255, (int)Math.Round(lo.R + frac * (hi.R - lo.R)))),
                Math.Max(0, Math.Min(255, (int)Math.Round(lo.G + frac * (hi.G - lo.G)))),
                Math.Max(0, Math.Min(255, (int)Math.Round(lo.B + frac * (hi.B - lo.B)))));
        }

        protected static List<Color> ComputeGradient(List<double> raw)
        {
            var gradient = new List<Color>(raw.Count);
            double rawMin = raw.Count > 0 ? raw[0] : 0.0;
            double rawMax = raw.Count > 0 ? raw[0] : 0.0;

            foreach (var value in raw)
            {
                if (value < rawMin) rawMin = value;
                if (value > rawMax) rawMax = value;
            }

            double range = rawMax - rawMin;
            foreach (var value in raw)
            {
                double t = range > 0.0 ? (value - rawMin) / range : 0.0;
                gradient.Add(InterpolateGradient(t));
            }

            return gradient;
        }

        /// <summary>
        /// Reverses a value range without changing its minimum or maximum.
        /// For example, [2, 5, 10] becomes [10, 7, 2].
        /// Invalid values are replaced with 0.0.
        /// </summary>
        protected static List<double> InvertValues(IList<double> values)
        {
            var inverted = new List<double>(values.Count);
            bool hasFiniteValue = false;
            double min = double.MaxValue;
            double max = double.MinValue;

            foreach (var value in values)
            {
                if (double.IsNaN(value) || double.IsInfinity(value)) continue;
                hasFiniteValue = true;
                if (value < min) min = value;
                if (value > max) max = value;
            }

            foreach (var value in values)
            {
                inverted.Add(!hasFiniteValue || double.IsNaN(value) || double.IsInfinity(value)
                    ? 0.0
                    : max - value + min);
            }

            return inverted;
        }

        protected void BuildPreviewData(VoxelGrid voxelGrid, List<double> raw)
        {
            _previewPoints = new List<Point3d>(raw.Count);
            _previewColors = ComputeGradient(raw);

            foreach (var key in voxelGrid.FilledKeys)
                _previewPoints.Add(voxelGrid.KeyToCenter(key));
        }

        public override void DrawViewportMeshes(IGH_PreviewArgs args) { }

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            if (Hidden || !IsPreviewCapable || _previewPoints.Count == 0) return;

            for (int i = 0; i < _previewPoints.Count && i < _previewColors.Count; i++)
            {
                args.Display.DrawPoint(
                    _previewPoints[i],
                    Rhino.Display.PointStyle.RoundSimple,
                    5, _previewColors[i]);
            }
        }

        public override bool IsPreviewCapable => true;

        public override BoundingBox ClippingBox
        {
            get
            {
                var bb = BoundingBox.Empty;
                foreach (var pt in _previewPoints) bb.Union(pt);
                return bb;
            }
        }

        protected VoxelGrid UnwrapVoxelGrid(object obj)
        {
            if (obj is VoxelGrid vg) return vg;
            if (obj is Grasshopper.Kernel.Types.GH_ObjectWrapper wrapper)
                return wrapper.Value as VoxelGrid;
            return null;
        }
    }
}
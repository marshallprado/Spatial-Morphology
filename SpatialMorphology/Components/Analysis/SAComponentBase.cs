// -*- coding: utf-8 -*-
// Version 1.1.0
using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace SpatialMorphology
{
    /// <summary>
    /// Shared base class for all SA components.
    /// Provides:
    ///   - Standard gradient (low=red to high=blue)
    ///   - Per-voxel gradient color computation
    ///   - Viewport preview as colored point cloud
    ///   - VoxelGrid unwrapper
    /// </summary>
    public abstract class SAComponentBase : GH_Component
    {
        // ── Stored preview data ───────────────────────────────────────────────
        protected List<Point3d> _previewPoints = new List<Point3d>();
        protected List<Color> _previewColors = new List<Color>();

        // ── Constructor ───────────────────────────────────────────────────────
        protected SAComponentBase(
            string name, string nickname, string description,
            string category, string subcategory)
            : base(name, nickname, description, category, subcategory)
        { }

        // ── Gradient color stops — low to high ────────────────────────────────
        protected static readonly Color[] GRADIENT_STOPS = new Color[]
        {
            Color.FromArgb(255, 234,  38,   0),   // low  — red
            Color.FromArgb(255, 234, 126,   0),   //      — orange
            Color.FromArgb(255, 254, 244,  84),   //      — yellow
            Color.FromArgb(255, 173, 203, 249),   //      — light blue
            Color.FromArgb(255,  75, 107, 169),   // high — blue
        };

        // ── Interpolate gradient ──────────────────────────────────────────────
        protected static Color InterpolateGradient(double t)
        {
            t = Math.Max(0.0, Math.Min(1.0, t));

            int nStops = GRADIENT_STOPS.Length;
            double scaled = t * (nStops - 1);
            int idxLo = (int)Math.Floor(scaled);
            int idxHi = Math.Min(idxLo + 1, nStops - 1);
            double frac = scaled - idxLo;

            Color lo = GRADIENT_STOPS[idxLo];
            Color hi = GRADIENT_STOPS[idxHi];

            int r = (int)Math.Round(lo.R + frac * (hi.R - lo.R));
            int g = (int)Math.Round(lo.G + frac * (hi.G - lo.G));
            int b = (int)Math.Round(lo.B + frac * (hi.B - lo.B));

            return Color.FromArgb(255,
                Math.Max(0, Math.Min(255, r)),
                Math.Max(0, Math.Min(255, g)),
                Math.Max(0, Math.Min(255, b)));
        }

        // ── Compute gradient colors for a raw value list ──────────────────────
        protected static List<Color> ComputeGradient(List<double> raw)
        {
            var gradient = new List<Color>(raw.Count);
            double rawMin = raw.Count > 0 ? raw[0] : 0;
            double rawMax = raw.Count > 0 ? raw[0] : 0;

            foreach (var v in raw)
            {
                if (v < rawMin) rawMin = v;
                if (v > rawMax) rawMax = v;
            }

            double range = rawMax - rawMin;

            foreach (var v in raw)
            {
                double t = range > 0 ? (v - rawMin) / range : 0.0;
                gradient.Add(InterpolateGradient(t));
            }

            return gradient;
        }

        // ── Build preview data from voxel grid and values ─────────────────────
        protected void BuildPreviewData(VoxelGrid voxelGrid, List<double> raw)
        {
            _previewPoints = new List<Point3d>(raw.Count);
            _previewColors = ComputeGradient(raw);

            var orderedKeys = voxelGrid.FilledKeys;
            foreach (var key in orderedKeys)
                _previewPoints.Add(voxelGrid.KeyToCenter(key));
        }

        // ── Viewport preview — shaded ─────────────────────────────────────────
        public override void DrawViewportMeshes(IGH_PreviewArgs args)
        {
            // Points only — no mesh drawing needed
        }

        // ── Viewport preview — wireframe ──────────────────────────────────────
        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            if (this.Hidden || !this.IsPreviewCapable) return;
            if (_previewPoints.Count == 0) return;

            for (int i = 0; i < _previewPoints.Count && i < _previewColors.Count; i++)
            {
                args.Display.DrawPoint(
                    _previewPoints[i],
                    Rhino.Display.PointStyle.RoundSimple,
                    5,
                    _previewColors[i]);
            }
        }

        // ── Preview capability ────────────────────────────────────────────────
        public override bool IsPreviewCapable => true;

        // ── Bounding box for preview ──────────────────────────────────────────
        public override BoundingBox ClippingBox
        {
            get
            {
                var bb = BoundingBox.Empty;
                foreach (var pt in _previewPoints)
                    bb.Union(pt);
                return bb;
            }
        }

        // ── VoxelGrid unwrapper ───────────────────────────────────────────────
        protected VoxelGrid UnwrapVoxelGrid(object obj)
        {
            if (obj is VoxelGrid vg) return vg;
            if (obj is Grasshopper.Kernel.Types.GH_ObjectWrapper wrapper)
                return wrapper.Value as VoxelGrid;
            return null;
        }
    }
}
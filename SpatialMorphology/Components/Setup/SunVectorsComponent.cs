// -*- coding: utf-8 -*-
// Version 1.1.0
using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace SpatialMorphology
{
    public class SunVectorsComponent : GH_Component
    {
        // ── Stored preview data ───────────────────────────────────────────────
        private List<Point3d> _sunPoints = new List<Point3d>();
        private List<Point3d> _pathPoints = new List<Point3d>();
        private Point3d _previewOrigin = Point3d.Origin;
        private double _previewRadius = 1.0;

        // ── Constructor ───────────────────────────────────────────────────────
        public SunVectorsComponent()
            : base(
                "SunVectors",
                "SunVec",
                "Computes sun vectors using the Solar Position Algorithm (SPA).\n\n" +
                "Outputs unit vectors pointing from ground toward the sun\n" +
                "(Ladybug convention — positive = sun above horizon).\n\n" +
                "Vectors below the horizon (altitude < 0) are excluded.\n\n" +
                "Connect voxel_grid to automatically center and scale\n" +
                "the sun path preview to match the analysis geometry.\n\n" +
                "Analysis periods:\n" +
                "  0 = Annual      (all 12 months)\n" +
                "  1 = Summer      (Jun, Jul, Aug)\n" +
                "  2 = Winter      (Dec, Jan, Feb)\n" +
                "  3 = Equinox     (Mar 21 and Sep 21 only)\n" +
                "  4 = Custom      (use start_month and end_month)\n\n" +
                "Version 1.1.0",
                "Spatial Morphology",
                "Setup")
        { }

        // ── GUID ──────────────────────────────────────────────────────────────
        public override Guid ComponentGuid =>
            new Guid("A7B8C9D0-E1F2-3456-ABCD-012345678914");

        // ── Icon ──────────────────────────────────────────────────────────────
        protected override Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream(
                    "SpatialMorphology.Resources.SunVectors_24.png");
                return stream != null ? new Bitmap(stream) : null;
            }
        }

        // ── Parameters ────────────────────────────────────────────────────────
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("voxel_grid", "VG",
                "Optional. VoxelGrid object from the VoxelGrid component.\n" +
                "When connected, the sun path preview is automatically\n" +
                "centered and scaled to match the analysis geometry.",
                GH_ParamAccess.item); 
            pManager.AddNumberParameter("latitude", "Lat",
                "Project latitude in decimal degrees.\n" +
                "Positive = North, Negative = South.\n" +
                "Example: Knoxville TN = 35.96, NYC = 40.71",
                GH_ParamAccess.item, 35.96);
            pManager.AddNumberParameter("longitude", "Lon",
                "Project longitude in decimal degrees.\n" +
                "Positive = East, Negative = West.\n" +
                "Example: Knoxville TN = -83.92, NYC = -74.01",
                GH_ParamAccess.item, -83.92);
            pManager.AddNumberParameter("time_zone", "TZ",
                "UTC time zone offset in hours.\n" +
                "Eastern = -5, Central = -6, Mountain = -7, Pacific = -8.",
                GH_ParamAccess.item, -5.0);
            pManager.AddNumberParameter("north", "N",
                "Rotation of north from Y axis in degrees clockwise.\n" +
                "0 = Y axis is north (default).\n" +
                "90 = X axis is north.",
                GH_ParamAccess.item, 0.0);
            pManager.AddIntegerParameter("analysis_period", "AP",
                "Analysis period:\n" +
                "  0 = Annual   (all 12 months)\n" +
                "  1 = Summer   (Jun, Jul, Aug)\n" +
                "  2 = Winter   (Dec, Jan, Feb)\n" +
                "  3 = Equinox  (Mar 21 and Sep 21 only)\n" +
                "  4 = Custom   (use start_month and end_month)",
                GH_ParamAccess.item, 0);
            pManager.AddIntegerParameter("start_month", "SM",
                "Start month (1-12). Used only when analysis_period = 4.",
                GH_ParamAccess.item, 1);
            pManager.AddIntegerParameter("end_month", "EM",
                "End month (1-12). Used only when analysis_period = 4.",
                GH_ParamAccess.item, 12);
            pManager.AddIntegerParameter("time_step", "TS",
                "Hours between sun position samples.\n" +
                "1 = hourly (most accurate).\n" +
                "2 = every 2 hours (faster).\n" +
                "Default: 1.",
                GH_ParamAccess.item, 1);
            

            pManager[8].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddVectorParameter("sun_vectors", "V",
                "Unit vectors pointing from ground toward sun.\n" +
                "Only above-horizon positions included (altitude >= 0).\n" +
                "Compatible with Ladybug convention.",
                GH_ParamAccess.list);
            pManager.AddPointParameter("sun_points", "P",
                "Sun positions on unit sphere for preview.\n" +
                "Parallel to sun_vectors.",
                GH_ParamAccess.list);
            pManager.AddTextParameter("info", "I",
                "Summary — location, period, vector count.",
                GH_ParamAccess.item);
        }

        // ── Solve ─────────────────────────────────────────────────────────────
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // ── Collect inputs ────────────────────────────────────────────────
            double latitude = 35.96;
            double longitude = -83.92;
            double timeZone = -5.0;
            double north = 0.0;
            int analysisPeriod = 0;
            int startMonth = 1;
            int endMonth = 12;
            int timeStep = 1;
            object voxelGridObj = null;

            DA.GetData(8, ref voxelGridObj); 
            DA.GetData(0, ref latitude);
            DA.GetData(1, ref longitude);
            DA.GetData(2, ref timeZone);
            DA.GetData(3, ref north);
            DA.GetData(4, ref analysisPeriod);
            DA.GetData(5, ref startMonth);
            DA.GetData(6, ref endMonth);
            DA.GetData(7, ref timeStep);
            

            // ── Validate ──────────────────────────────────────────────────────
            latitude = Math.Max(-90.0, Math.Min(90.0, latitude));
            longitude = Math.Max(-180.0, Math.Min(180.0, longitude));
            timeZone = Math.Max(-12.0, Math.Min(14.0, timeZone));
            analysisPeriod = Math.Max(0, Math.Min(4, analysisPeriod));
            startMonth = Math.Max(1, Math.Min(12, startMonth));
            endMonth = Math.Max(1, Math.Min(12, endMonth));
            timeStep = Math.Max(1, Math.Min(24, timeStep));

            if (startMonth > endMonth && analysisPeriod == 4)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "start_month > end_month — swapping values.");
                int temp = startMonth;
                startMonth = endMonth;
                endMonth = temp;
            }

            // ── Unwrap VoxelGrid for preview scaling ──────────────────────────
            VoxelGrid voxelGrid = null;
            if (voxelGridObj != null)
            {
                voxelGrid = voxelGridObj as VoxelGrid;
                if (voxelGrid == null &&
                    voxelGridObj is Grasshopper.Kernel.Types.GH_ObjectWrapper w)
                    voxelGrid = w.Value as VoxelGrid;
            }

            // ── Compute preview origin and radius from voxel grid ─────────────
            if (voxelGrid != null)
            {
                // Use bounding box of all voxel centers to find grid center
                var keys = voxelGrid.FilledKeys;
                if (keys.Count > 0)
                {
                    double minX = double.MaxValue, maxX = double.MinValue;
                    double minY = double.MaxValue, maxY = double.MinValue;
                    double minZ = double.MaxValue, maxZ = double.MinValue;

                    foreach (var key in keys)
                    {
                        var pt = voxelGrid.KeyToCenter(key);
                        if (pt.X < minX) minX = pt.X;
                        if (pt.X > maxX) maxX = pt.X;
                        if (pt.Y < minY) minY = pt.Y;
                        if (pt.Y > maxY) maxY = pt.Y;
                        if (pt.Z < minZ) minZ = pt.Z;
                        if (pt.Z > maxZ) maxZ = pt.Z;
                    }

                    // Center of the bounding box
                    _previewOrigin = new Point3d(
                        (minX + maxX) / 2.0,
                        (minY + maxY) / 2.0,
                        (minZ + maxZ) / 2.0);

                    // Radius = half the longest diagonal of the bounding box
                    double dx = maxX - minX;
                    double dy = maxY - minY;
                    double dz = maxZ - minZ;
                    _previewRadius = Math.Sqrt(dx * dx + dy * dy + dz * dz) / 2.0;

                    // Minimum radius to ensure visibility
                    _previewRadius = Math.Max(_previewRadius, voxelGrid.VoxelSize * 5);
                }
            }
            else
            {
                // Default — unit sphere at world origin
                _previewOrigin = Point3d.Origin;
                _previewRadius = 1.0;
            }

            // ── Determine months to analyze ───────────────────────────────────
            List<int> months = GetMonths(analysisPeriod, startMonth, endMonth);

            // ── Compute sun positions ─────────────────────────────────────────
            var sunVectors = new List<Vector3d>();
            var sunPoints = new List<Point3d>();
            var pathPts = new List<Point3d>();
            int year = 2024;
            int totalBelow = 0;

            double northRad = north * Math.PI / 180.0;

            foreach (int month in months)
            {
                int day = 21;

                for (int hour = 0; hour < 24; hour += timeStep)
                {
                    double altitude, azimuth;
                    ComputeSunPosition(
                        year, month, day,
                        hour, 0, 0,
                        latitude, longitude, timeZone,
                        out altitude, out azimuth);

                    // ── Compute direction vector ───────────────────────────────
                    double altRad = altitude * Math.PI / 180.0;
                    double azmRad = azimuth * Math.PI / 180.0;

                    double vx = Math.Sin(azmRad) * Math.Cos(altRad);
                    double vy = Math.Cos(azmRad) * Math.Cos(altRad);
                    double vz = Math.Sin(altRad);

                    // Apply north rotation
                    double vxr = vx * Math.Cos(northRad) - vy * Math.Sin(northRad);
                    double vyr = vx * Math.Sin(northRad) + vy * Math.Cos(northRad);

                    // ── Path point — scaled and centered to voxel grid ─────────
                    var scaledPt = new Point3d(
                        _previewOrigin.X + vxr * _previewRadius,
                        _previewOrigin.Y + vyr * _previewRadius,
                        _previewOrigin.Z + vz * _previewRadius);

                    // Track all positions above -10° for smooth path arc
                    if (altitude > -10)
                        pathPts.Add(scaledPt);

                    // ── Only include above-horizon vectors ─────────────────────
                    if (altitude < 0)
                    {
                        totalBelow++;
                        continue;
                    }

                    var vec = new Vector3d(vxr, vyr, vz);
                    vec.Unitize();

                    sunVectors.Add(vec);
                    sunPoints.Add(scaledPt);
                }
            }

            // ── Store for preview ─────────────────────────────────────────────
            _sunPoints = sunPoints;
            _pathPoints = pathPts;

            // ── Info ──────────────────────────────────────────────────────────
            string[] periodNames =
            {
                "Annual", "Summer", "Winter", "Equinox", "Custom"
            };

            string monthNames = string.Join(", ",
                months.ConvertAll(m => System.Globalization.CultureInfo
                    .CurrentCulture.DateTimeFormat.GetMonthName(m)));

            string info = string.Format(
                "SunVectors | {0}\n" +
                "Location  : lat={1:F2} lon={2:F2} tz={3:+0;-0}\n" +
                "North     : {4:F1}° CW from Y axis\n" +
                "Period    : {5} | months: {6}\n" +
                "Time step : {7}h | vectors above horizon: {8}\n" +
                "Below horizon (excluded): {9}\n" +
                "Preview   : origin=({10:F1},{11:F1},{12:F1}) radius={13:F1}",
                periodNames[analysisPeriod],
                latitude, longitude, timeZone,
                north,
                periodNames[analysisPeriod], monthNames,
                timeStep,
                sunVectors.Count,
                totalBelow,
                _previewOrigin.X, _previewOrigin.Y, _previewOrigin.Z,
                _previewRadius);

            // ── Output ────────────────────────────────────────────────────────
            DA.SetDataList(0, sunVectors);
            DA.SetDataList(1, sunPoints);
            DA.SetData(2, info);
        }

        // ── Get months for analysis period ────────────────────────────────────
        private List<int> GetMonths(int period, int start, int end)
        {
            switch (period)
            {
                case 1: return new List<int> { 6, 7, 8 };
                case 2: return new List<int> { 12, 1, 2 };
                case 3: return new List<int> { 3, 9 };
                case 4:
                    var custom = new List<int>();
                    for (int m = start; m <= end; m++)
                        custom.Add(m);
                    return custom;
                default:
                    return new List<int> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
            }
        }

        // ── Solar Position Algorithm (SPA) ────────────────────────────────────
        private void ComputeSunPosition(
            int year, int month, int day,
            int hour, int minute, int second,
            double latitude, double longitude, double timeZone,
            out double altitudeDeg, out double azimuthDeg)
        {
            double jd = JulianDay(year, month, day,
                hour + minute / 60.0 + second / 3600.0 - timeZone);
            double jc = (jd - 2451545.0) / 36525.0;

            double l0 = (280.46646 + jc * (36000.76983 + jc * 0.0003032)) % 360.0;
            double m = 357.52911 + jc * (35999.05029 - 0.0001537 * jc);
            double mRad = m * Math.PI / 180.0;

            double c = Math.Sin(mRad) * (1.914602 - jc * (0.004817 + 0.000014 * jc))
                     + Math.Sin(2 * mRad) * (0.019993 - 0.000101 * jc)
                     + Math.Sin(3 * mRad) * 0.000289;

            double sunLon = l0 + c;
            double omega = 125.04 - 1934.136 * jc;
            double lambdaDeg = sunLon - 0.00569
                             - 0.00478 * Math.Sin(omega * Math.PI / 180.0);
            double lambda = lambdaDeg * Math.PI / 180.0;

            double eps0 = 23.0 + (26.0 + (21.448 - jc *
                (46.8150 + jc * (0.00059 - jc * 0.001813))) / 60.0) / 60.0;
            double eps = (eps0 + 0.00256 * Math.Cos(omega * Math.PI / 180.0))
                          * Math.PI / 180.0;

            double ra = Math.Atan2(Math.Cos(eps) * Math.Sin(lambda), Math.Cos(lambda));
            double dec = Math.Asin(Math.Sin(eps) * Math.Sin(lambda));

            double gmst = 280.46061837 + 360.98564736629 * (jd - 2451545.0)
                        + jc * jc * (0.000387933 - jc / 38710000.0);
            gmst = gmst % 360.0;

            double lha = (gmst + longitude - ra * 180.0 / Math.PI) * Math.PI / 180.0;
            double latRad = latitude * Math.PI / 180.0;

            double sinAlt = Math.Sin(latRad) * Math.Sin(dec)
                           + Math.Cos(latRad) * Math.Cos(dec) * Math.Cos(lha);
            double altitude = Math.Asin(sinAlt);
            double altDeg = altitude * 180.0 / Math.PI;

            double refraction = 0.0;
            if (altDeg > -0.575)
            {
                if (altDeg > 5.0)
                    refraction = 58.1 / Math.Tan(altitude)
                               - 0.07 / Math.Pow(Math.Tan(altitude), 3)
                               + 0.000086 / Math.Pow(Math.Tan(altitude), 5);
                else
                    refraction = altDeg *
                        (-518.2 + altDeg * (103.4 + altDeg * (-12.79 + altDeg * 0.711)))
                        + 1735.0;
                refraction /= 3600.0;
            }
            altitudeDeg = altDeg + refraction;

            double cosAzNum = Math.Sin(latRad) * Math.Cos(dec) * Math.Cos(lha)
                            - Math.Cos(latRad) * Math.Sin(dec);
            double cosAzDen = Math.Cos(altitude);
            double azimuth = Math.Atan2(Math.Sin(lha), cosAzNum / cosAzDen);
            azimuthDeg = (azimuth * 180.0 / Math.PI + 180.0) % 360.0;
        }

        // ── Julian Day Number ─────────────────────────────────────────────────
        private double JulianDay(int year, int month, int day, double ut)
        {
            if (month <= 2) { year -= 1; month += 12; }
            int a = year / 100;
            int b = 2 - a + a / 4;
            return Math.Floor(365.25 * (year + 4716))
                 + Math.Floor(30.6001 * (month + 1))
                 + day + ut / 24.0 + b - 1524.5;
        }

        // ── Viewport preview ──────────────────────────────────────────────────
        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            if (this.Hidden || !this.IsPreviewCapable) return;

            // Draw sun path arc in yellow
            var pathColor = Color.FromArgb(255, 255, 200, 0);
            if (_pathPoints.Count > 1)
            {
                for (int i = 0; i < _pathPoints.Count - 1; i++)
                    args.Display.DrawLine(
                        _pathPoints[i], _pathPoints[i + 1],
                        pathColor, 1);
            }

            // Draw sun position dots in orange
            var sunColor = Color.FromArgb(255, 255, 140, 0);
            foreach (var pt in _sunPoints)
                args.Display.DrawPoint(
                    pt,
                    Rhino.Display.PointStyle.RoundSimple,
                    4,
                    sunColor);

            // Draw vertical line from origin to show scale reference
            if (_previewRadius > 1.0)
            {
                var axisColor = Color.FromArgb(100, 255, 255, 255);
                args.Display.DrawLine(
                    _previewOrigin,
                    new Point3d(
                        _previewOrigin.X,
                        _previewOrigin.Y,
                        _previewOrigin.Z + _previewRadius),
                    axisColor, 1);
            }
        }

        public override void DrawViewportMeshes(IGH_PreviewArgs args) { }

        public override bool IsPreviewCapable => true;

        public override BoundingBox ClippingBox
        {
            get
            {
                var bb = BoundingBox.Empty;
                foreach (var pt in _sunPoints) bb.Union(pt);
                foreach (var pt in _pathPoints) bb.Union(pt);
                return bb;
            }
        }
    }
}
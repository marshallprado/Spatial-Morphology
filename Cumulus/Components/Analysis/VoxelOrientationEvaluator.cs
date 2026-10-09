// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino.Geometry;

namespace Cumulus
{
    /// <summary>
    /// Shared face-neighbour orientation evaluation used by Surface Direction
    /// and Surface Alignment. This is an internal helper, not a Grasshopper component.
    /// </summary>
    internal static class VoxelOrientationEvaluator
    {
        internal static readonly int[] DeltaX = { 1, -1, 0, 0, 0, 0 };
        internal static readonly int[] DeltaY = { 0, 0, 1, -1, 0, 0 };
        internal static readonly int[] DeltaZ = { 0, 0, 0, 0, 1, -1 };

        internal static readonly string[] DirectionNames =
        {
            "+X (East)", "-X (West)",
            "+Y (North)", "-Y (South)",
            "+Z (Up)", "-Z (Down)"
        };

        internal static readonly Vector3d[] DirectionVectors =
        {
            new Vector3d( 1,  0,  0),
            new Vector3d(-1,  0,  0),
            new Vector3d( 0,  1,  0),
            new Vector3d( 0, -1,  0),
            new Vector3d( 0,  0,  1),
            new Vector3d( 0,  0, -1)
        };

        private static readonly Color[] DirectionColors =
        {
            Color.FromArgb(255, 225,  87,  89), // +X
            Color.FromArgb(255,  69, 123, 157), // -X
            Color.FromArgb(255, 237, 191,  57), // +Y
            Color.FromArgb(255,  43, 147,  72), // -Y
            Color.FromArgb(255, 148, 103, 189), // +Z
            Color.FromArgb(255, 110, 110, 110)  // -Z
        };

        internal static OrientationEvaluation Evaluate(VoxelGrid voxelGrid)
        {
            var orderedKeys = voxelGrid.FilledKeys;
            var filled = voxelGrid.FilledKeysSet;

            var result = new OrientationEvaluation(orderedKeys.Count);

            foreach (var key in orderedKeys)
            {
                int x = key.Item1;
                int y = key.Item2;
                int z = key.Item3;

                int openFaceCount = 0;
                int dominantDirection = -1;
                var aggregateNormal = Vector3d.Zero;

                for (int direction = 0; direction < 6; direction++)
                {
                    var neighbour = (
                        x + DeltaX[direction],
                        y + DeltaY[direction],
                        z + DeltaZ[direction]);

                    if (filled.Contains(neighbour))
                        continue;

                    openFaceCount++;

                    // The first open face preserves the legacy deterministic
                    // direction-category tie-break order.
                    if (dominantDirection < 0)
                        dominantDirection = direction;

                    aggregateNormal += DirectionVectors[direction];
                }

                result.OpenFaceCounts.Add(openFaceCount);
                result.DirectionIndices.Add(dominantDirection);

                if (dominantDirection < 0)
                {
                    result.AggregateNormals.Add(Vector3d.Zero);
                    result.CategoryColors.Add(Color.FromArgb(255, 45, 45, 45));
                    result.InteriorCount++;
                    continue;
                }

                result.DirectionCounts[dominantDirection]++;
                result.CategoryColors.Add(DirectionColors[dominantDirection]);

                if (aggregateNormal.Length < 1e-12)
                {
                    result.AggregateNormals.Add(Vector3d.Zero);
                    result.CancellingNormalCount++;
                }
                else
                {
                    aggregateNormal.Unitize();
                    result.AggregateNormals.Add(aggregateNormal);
                }
            }

            return result;
        }
    }

    /// <summary>Immutable-shape result container for voxel face-orientation data.</summary>
    internal sealed class OrientationEvaluation
    {
        internal OrientationEvaluation(int capacity)
        {
            DirectionIndices = new List<int>(capacity);
            AggregateNormals = new List<Vector3d>(capacity);
            OpenFaceCounts = new List<int>(capacity);
            CategoryColors = new List<Color>(capacity);
            DirectionCounts = new int[6];
        }

        internal List<int> DirectionIndices { get; private set; }
        internal List<Vector3d> AggregateNormals { get; private set; }
        internal List<int> OpenFaceCounts { get; private set; }
        internal List<Color> CategoryColors { get; private set; }
        internal int[] DirectionCounts { get; private set; }
        internal int InteriorCount { get; set; }
        internal int CancellingNormalCount { get; set; }
    }
}

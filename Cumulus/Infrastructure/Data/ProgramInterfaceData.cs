// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using Rhino.Geometry;

namespace Cumulus.Infrastructure.Data
{
    /// <summary>
    /// World-space metadata for one exact canonical voxel interface face.
    /// ProgramA-to-ProgramB gives the canonical face-normal orientation.
    /// </summary>
    public sealed class ProgramInterfaceData
    {
        public int ProgramAIndex { get; private set; }
        public int ProgramBIndex { get; private set; }
        public string ProgramAName { get; private set; }
        public string ProgramBName { get; private set; }
        public int VoxelAIndex { get; private set; }
        public int VoxelBIndex { get; private set; }
        public int GridAxis { get; private set; }
        public int GridDirection { get; private set; }
        public int GridZ { get; private set; }
        public Point3d Center { get; private set; }
        public Vector3d Normal { get; private set; }
        public double Area { get; private set; }
        public bool IsCoreInterface { get; private set; }
        /// <summary>True when this face closes a top/bottom open program boundary, not a program adjacency.</summary>
        public bool IsHorizontalOpenBoundaryCap { get; private set; }
        /// <summary>True when a horizontal cap faces outside the VoxelGrid domain rather than an unallocated voxel.</summary>
        public bool IsExteriorCap { get; private set; }

        public ProgramInterfaceData(
            int programAIndex,
            int programBIndex,
            string programAName,
            string programBName,
            int voxelAIndex,
            int voxelBIndex,
            int gridAxis,
            int gridDirection,
            int gridZ,
            Point3d center,
            Vector3d normal,
            double area,
            bool isCoreInterface,
            bool isHorizontalOpenBoundaryCap,
            bool isExteriorCap)
        {
            ProgramAIndex = programAIndex;
            ProgramBIndex = programBIndex;
            ProgramAName = programAName;
            ProgramBName = programBName;
            VoxelAIndex = voxelAIndex;
            VoxelBIndex = voxelBIndex;
            GridAxis = gridAxis;
            GridDirection = gridDirection;
            GridZ = gridZ;
            Center = center;
            Normal = normal;
            Area = area;
            IsCoreInterface = isCoreInterface;
            IsHorizontalOpenBoundaryCap = isHorizontalOpenBoundaryCap;
            IsExteriorCap = isExteriorCap;
        }

        public override string ToString()
        {
            if (IsHorizontalOpenBoundaryCap)
            {
                return string.Format(
                    "{0} [{1}] -> {2} cap | area={3:F3} | voxel={4}",
                    ProgramAName,
                    ProgramAIndex,
                    IsExteriorCap ? "Exterior" : "OpenBoundary",
                    Area,
                    VoxelAIndex);
            }

            return string.Format(
                "{0} [{1}] <-> {2} [{3}] | area={4:F3} | voxel={5}<->{6}",
                ProgramAName,
                ProgramAIndex,
                ProgramBName,
                ProgramBIndex,
                Area,
                VoxelAIndex,
                VoxelBIndex);
        }
    }

    /// <summary>One weighted edge in the direct program/core adjacency graph.</summary>
    public sealed class ProgramAdjacencyEdgeData
    {
        public int ProgramAIndex { get; private set; }
        public int ProgramBIndex { get; private set; }
        public string ProgramAName { get; private set; }
        public string ProgramBName { get; private set; }
        public int FaceCount { get; private set; }
        public double Area { get; private set; }
        public bool IsCoreAdjacency { get; private set; }

        public ProgramAdjacencyEdgeData(
            int programAIndex,
            int programBIndex,
            string programAName,
            string programBName,
            int faceCount,
            double area)
        {
            ProgramAIndex = programAIndex;
            ProgramBIndex = programBIndex;
            ProgramAName = programAName;
            ProgramBName = programBName;
            FaceCount = faceCount;
            Area = area;
            IsCoreAdjacency = programAIndex == -2 || programBIndex == -2;
        }

        public override string ToString()
        {
            return string.Format(
                "{0} <-> {1} | faces={2} | area={3:F3}",
                ProgramAName,
                ProgramBName,
                FaceCount,
                Area);
        }
    }

    /// <summary>Exact direct-adjacency graph derived from the canonical interface faces.</summary>
    public sealed class ProgramAdjacencyGraphData
    {
        public IReadOnlyList<ProgramAdjacencyEdgeData> Edges { get; private set; }

        public ProgramAdjacencyGraphData(IReadOnlyList<ProgramAdjacencyEdgeData> edges)
        {
            Edges = edges ?? throw new ArgumentNullException(nameof(edges));
        }

        public override string ToString()
        {
            return string.Format("ProgramAdjacencyGraph(edges={0})", Edges.Count);
        }
    }
}

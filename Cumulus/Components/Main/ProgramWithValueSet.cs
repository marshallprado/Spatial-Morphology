// -*- coding: utf-8 -*-
using System;

namespace Cumulus
{
    /// <summary>
    /// Couples a program definition with its per-channel scoring weights.
    /// Produced by the ValueSet component and consumed directly by AnalysisStack.
    /// </summary>
    public sealed class ProgramWithValueSet
    {
        /// <summary>The program's display and allocation definition.</summary>
        public ProgramDefinition Program { get; }

        /// <summary>The program's analysis-channel multipliers.</summary>
        public ValueSet ValueSet { get; }

        /// <summary>Creates a coupled program and ValueSet input.</summary>
        public ProgramWithValueSet(ProgramDefinition program, ValueSet valueSet)
        {
            Program = program ?? throw new ArgumentNullException(nameof(program));
            ValueSet = valueSet ?? throw new ArgumentNullException(nameof(valueSet));

            if (!string.Equals(Program.Name, ValueSet.ProgramName,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "ProgramDefinition.Name must match ValueSet.ProgramName.",
                    nameof(valueSet));
            }
        }

        /// <summary>Returns a concise display description for Grasshopper panels.</summary>
        public override string ToString()
        {
            return string.Format(
                "ProgramWithValueSet(program='{0}', voxel_count={1}, channels={2})",
                Program.Name,
                Program.VoxelCount,
                ValueSet.Weights.Count);
        }
    }
}

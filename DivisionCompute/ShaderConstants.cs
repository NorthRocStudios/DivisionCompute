//
// Copyright (c) 2026 Rex Woodfield and DivisionCompute contributors
//
// This file is part of DivisionCompute and is subject to the terms
// of the DivisionCompute License. See the LICENSE.txt file in the
// project root for full license terms.
//
namespace DivisionEngine
{
    /// <summary>
    /// Constants shared by the runtime, the translator, and the source generator.
    /// </summary>
    /// <remarks>
    /// This file is compiled into both DivisionCompute and the source generator so
    /// the HLSL emitted at build time and at runtime cannot drift apart.
    /// </remarks>
    internal static class DivisionShaderConstants
    {
        /// <summary>
        /// Name of the constant buffer that holds a shader's plain fields.
        /// </summary>
        internal const string UniformBufferName = "DivisionParams";

        /// <summary>
        /// Suffix applied to generated structured-buffer length helper functions.
        /// </summary>
        internal const string BufferLengthHelperPrefix = "DivisionBufferLength_";
    }
}

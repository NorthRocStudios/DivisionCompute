//
// Copyright (c) 2026 Rex Woodfield and DivisionCompute contributors
//
// This file is part of DivisionCompute and is subject to the terms
// of the DivisionCompute License. See the LICENSE.txt file in the project root
// for full license terms.
//
namespace DivisionEngine
{
    /// <summary>
    /// Severity of a message raised through <see cref="DivisionShaderCompiler.Log"/>.
    /// </summary>
    public enum ShaderLogLevel
    {
        /// <summary>A translation/compilation/cache event worth reporting.</summary>
        Info,

        /// <summary>Compilation succeeded but the compiler reported warnings.</summary>
        Warning,

        /// <summary>A shader failed to translate or compile.</summary>
        Error,
    }
}

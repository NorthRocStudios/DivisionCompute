//
// Copyright (c) 2026 Rex Woodfield and DivisionCompute contributors
//
// This file is part of DivisionCompute and is subject to the terms
// of the DivisionCompute License. See the LICENSE.txt file in the
// project root for full license terms.
//
using System.Collections.Generic;

namespace DivisionEngine
{
    /// <summary>
    /// The kind of GPU resource a shader field binds to.
    /// </summary>
    public enum DivisionResourceKind
    {
        /// <summary>A shader-resource view bound to <c>tN</c>.</summary>
        Srv = 0,

        /// <summary>An unordered-access view bound to <c>uN</c>.</summary>
        Uav = 1,
    }

    /// <summary>
    /// One resource binding produced by the translator or the source generator.
    /// </summary>
    /// <param name="Name">The C# field name of the resource.</param>
    /// <param name="Slot">The HLSL register slot (<c>tN</c> or <c>uN</c>).</param>
    /// <param name="Kind">Whether the resource is read (SRV) or written (UAV).</param>
    public readonly record struct DivisionResourceBinding(string Name, uint Slot, DivisionResourceKind Kind);

    /// <summary>
    /// One shader uniform after HLSL constant-buffer packing.
    /// </summary>
    /// <param name="Name">The C# field name, and the HLSL member name.</param>
    /// <param name="Kind">The HLSL type.</param>
    /// <param name="Size">Size in bytes.</param>
    /// <param name="Offset">Packed offset inside the parameter constant buffer.</param>
    public readonly record struct DivisionUniformBinding(string Name, UniformKind Kind, int Size, int Offset);

    /// <summary>
    /// Everything a shader needs at runtime, produced at build time by the
    /// DivisionCompute source generator.
    /// </summary>
    /// <remarks>
    /// When a descriptor is registered for a shader type, the runtime never starts
    /// Roslyn: <see cref="DivisionShaderCompiler.TranslateShader"/> returns the baked
    /// HLSL, and <see cref="DivisionComputeContext.CreateShader"/> uses the baked
    /// resource and uniform layouts instead of reflection. Runtime translation stays
    /// available as a fallback (hot reload, shaders compiled after the build).
    /// </remarks>
    public sealed class DivisionShaderDescriptor
    {
        /// <summary>
        /// Creates a descriptor from data produced by the source generator.
        /// </summary>
        /// <param name="shaderTypeName">The name of the originating C# shader struct.</param>
        /// <param name="hlslCode">The generated HLSL source.</param>
        /// <param name="kernelNames">The entry points, in declaration order.</param>
        /// <param name="resources">The resource bindings, in declaration order.</param>
        /// <param name="uniforms">The packed uniform fields, in declaration order.</param>
        /// <param name="uniformByteSize">The size of the parameter constant buffer in bytes.</param>
        /// <param name="sourceFilePath">
        /// Path of the C# file the shader was translated from, when known.
        /// </param>
        /// <param name="sourceHash">
        /// SHA-256 of the source file at build time, used to detect a shader edited
        /// after the build (hot reload) and fall back to the runtime translator.
        /// </param>
        public DivisionShaderDescriptor(
            string shaderTypeName,
            string hlslCode,
            IReadOnlyList<string> kernelNames,
            IReadOnlyList<DivisionResourceBinding> resources,
            IReadOnlyList<DivisionUniformBinding> uniforms,
            int uniformByteSize,
            string? sourceFilePath = null,
            string? sourceHash = null)
        {
            ShaderTypeName = shaderTypeName;
            HlslCode = hlslCode;
            KernelNames = kernelNames;
            Resources = resources;
            Uniforms = uniforms;
            UniformByteSize = uniformByteSize;
            SourceFilePath = sourceFilePath;
            SourceHash = sourceHash;
        }

        /// <summary>
        /// The name of the originating C# shader struct.
        /// </summary>
        public string ShaderTypeName { get; }

        /// <summary>
        /// The generated HLSL source.
        /// </summary>
        public string HlslCode { get; }

        /// <summary>
        /// The kernel entry points, in declaration order.
        /// </summary>
        public IReadOnlyList<string> KernelNames { get; }

        /// <summary>
        /// The resource bindings, in declaration order.
        /// </summary>
        public IReadOnlyList<DivisionResourceBinding> Resources { get; }

        /// <summary>
        /// The packed uniform fields, in declaration order.
        /// </summary>
        public IReadOnlyList<DivisionUniformBinding> Uniforms { get; }

        /// <summary>
        /// The size of the parameter constant buffer in bytes.
        /// </summary>
        public int UniformByteSize { get; }

        /// <summary>
        /// Path of the C# file the shader was translated from, or <c>null</c> when the
        /// generator could not determine it.
        /// </summary>
        public string? SourceFilePath { get; }

        /// <summary>
        /// SHA-256 of the source file at build time, or <c>null</c> when unavailable.
        /// </summary>
        public string? SourceHash { get; }
    }
}

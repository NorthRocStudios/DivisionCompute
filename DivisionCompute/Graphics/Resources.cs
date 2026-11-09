//
// Copyright (c) 2026 Rex Woodfield and DivisionCompute contributors
//
// This file is part of DivisionCompute and is subject to the terms
// of the DivisionCompute License. See the LICENSE.txt file in the
// project root for full license terms.
//
using DivisionEngine.MathLib;

namespace DivisionEngine.Graphics
{
    // NOTE: These types are both compile-time shader markers and, for buffers and
    // textures, runtime resource owners. Roslyn walks the shader struct's syntax
    // tree to translate the field declarations to HLSL; the CPU-side members live
    // in the partial files next to this one. Indexers are shader-only and throw if
    // called on the CPU - use GetData/SetData for CPU access.

    /// <summary>
    /// Read-only 2D texture. Declare as a <c>[ShaderResource]</c> field in a shader struct
    /// to bind a sampled input texture; translates to HLSL <c>Texture2D&lt;T&gt;</c>.
    /// On the CPU, owns a D3D11 texture and its shader-resource view.
    /// </summary>
    public partial class Texture2D<T> where T : unmanaged
    {
        /// <summary>Reads a texel by integer coordinate. Shader-only.</summary>
        public T this[int2 coord] => throw new NotSupportedException();
        /// <summary>Reads a texel by unsigned coordinate. Shader-only.</summary>
        public T this[uint2 coord] => throw new NotSupportedException();
        /// <summary>Reads a texel by integer x/y coordinate. Shader-only.</summary>
        public T this[int x, int y] => throw new NotSupportedException();
        /// <summary>Reads a texel by unsigned x/y coordinate. Shader-only.</summary>
        public T this[uint x, uint y] => throw new NotSupportedException();
    }

    /// <summary>
    /// Read-write 2D texture. Declare as a <c>[ShaderResource]</c> field to bind a
    /// compute-shader output texture; translates to HLSL <c>RWTexture2D&lt;T&gt;</c>.
    /// On the CPU, owns a D3D11 texture plus UAV and SRV views.
    /// </summary>
    public sealed partial class RWTexture2D<T> where T : unmanaged
    {
        /// <summary>Reads or writes a texel by integer coordinate. Shader-only.</summary>
        public T this[int2 coord]
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        /// <summary>Reads or writes a texel by unsigned coordinate. Shader-only.</summary>
        public T this[uint2 coord]
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        /// <summary>Reads or writes a texel by integer x/y coordinate. Shader-only.</summary>
        public T this[int x, int y]
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        /// <summary>Reads or writes a texel by unsigned x/y coordinate. Shader-only.</summary>
        public T this[uint x, uint y]
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Read-only 3D texture. Translates to HLSL <c>Texture3D&lt;T&gt;</c>.
    /// On the CPU, owns a D3D11 texture and its shader-resource view.
    /// </summary>
    public partial class Texture3D<T> where T : unmanaged
    {
        /// <summary>Reads a voxel by integer coordinate. Shader-only.</summary>
        public T this[int3 coord] => throw new NotSupportedException();
        /// <summary>Reads a voxel by unsigned coordinate. Shader-only.</summary>
        public T this[uint3 coord] => throw new NotSupportedException();
    }

    /// <summary>
    /// Read-write 3D texture. Translates to HLSL <c>RWTexture3D&lt;T&gt;</c>.
    /// On the CPU, owns a D3D11 texture plus UAV and SRV views.
    /// </summary>
    public sealed partial class RWTexture3D<T> where T : unmanaged
    {
        /// <summary>Reads or writes a voxel by integer coordinate. Shader-only.</summary>
        public T this[int3 coord]
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        /// <summary>Reads or writes a voxel by unsigned coordinate. Shader-only.</summary>
        public T this[uint3 coord]
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Read-only typed buffer. Translates to HLSL <c>Buffer&lt;T&gt;</c>.
    /// </summary>
    public sealed class Buffer<T>
    {
        /// <summary>Reads the element at <paramref name="index"/>. Shader-only.</summary>
        public T this[int index] => throw new NotSupportedException();
        /// <summary>Reads the element at <paramref name="index"/>. Shader-only.</summary>
        public T this[uint index] => throw new NotSupportedException();
    }

    /// <summary>
    /// Read-write typed buffer. Translates to HLSL <c>RWBuffer&lt;T&gt;</c>.
    /// </summary>
    public sealed class RWBuffer<T>
    {
        /// <summary>Reads or writes the element at <paramref name="index"/>. Shader-only.</summary>
        public T this[int index]
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        /// <summary>Reads or writes the element at <paramref name="index"/>. Shader-only.</summary>
        public T this[uint index]
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Read-only structured buffer of arbitrary structs. Translates to HLSL
    /// <c>StructuredBuffer&lt;T&gt;</c>. On the CPU, owns a D3D11 structured buffer
    /// and its shader-resource view.
    /// </summary>
    /// <typeparam name="T">
    /// An unmanaged, sequential struct. See the structured-buffer documentation for
    /// the supported field types and layout rules.
    /// </typeparam>
    public partial class StructuredBuffer<T> where T : unmanaged
    {
        /// <summary>Reads the element at <paramref name="index"/>. Shader-only.</summary>
        public T this[int index] => throw new NotSupportedException();

        /// <summary>Reads the element at <paramref name="index"/>. Shader-only.</summary>
        public T this[uint index] => throw new NotSupportedException();
    }

    /// <summary>
    /// Read-write structured buffer of arbitrary structs. Translates to HLSL
    /// <c>RWStructuredBuffer&lt;T&gt;</c>. On the CPU, owns a D3D11 structured
    /// buffer plus UAV and SRV views.
    /// </summary>
    /// <typeparam name="T">
    /// An unmanaged, sequential struct. See the structured-buffer documentation for
    /// the supported field types and layout rules.
    /// </typeparam>
    public sealed partial class RWStructuredBuffer<T> where T : unmanaged
    {
        /// <summary>Reads or writes the element at <paramref name="index"/>. Shader-only.</summary>
        public T this[int index]
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>Reads or writes the element at <paramref name="index"/>. Shader-only.</summary>
        public T this[uint index]
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Constant (uniform) buffer. Translates to an HLSL <c>cbuffer</c> block on
    /// <c>register(b0)</c>, <c>b1</c>, … in declaration order.
    /// </summary>
    public sealed class ConstantBuffer<T> { }

    /// <summary>
    /// Sampler state marker. Emits an HLSL <c>SamplerState</c> declaration. Not
    /// currently used for anything by the runtime - read-only textures sample
    /// through the translator-injected <c>DivisionDefaultSampler</c>.
    /// </summary>
    public sealed class Sampler { }
}

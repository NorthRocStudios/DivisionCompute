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
    // NOTE: These are compile-only markers. Their bodies never execute -
    // Roslyn walks the shader struct's syntax tree to translate it to HLSL,
    // and then we bind real D3D11 resources by field name at dispatch time.
    // The indexers exist only so the C# in the shader struct compiles cleanly.

    /// <summary>
    /// Read-only 2D texture. Declare as a <c>[ShaderResource]</c> field in a shader struct
    /// to bind a sampled input texture; translates to HLSL <c>Texture2D&lt;T&gt;</c>.
    /// </summary>
    public sealed class Texture2D<T>
    {
        public T this[int2 coord] => throw new NotSupportedException();
        public T this[uint2 coord] => throw new NotSupportedException();
        public T this[int x, int y] => throw new NotSupportedException();
        public T this[uint x, uint y] => throw new NotSupportedException();
    }

    /// <summary>
    /// Read-write 2D texture. Declare as a <c>[ShaderResource]</c> field to bind a
    /// compute-shader output texture; translates to HLSL <c>RWTexture2D&lt;T&gt;</c>.
    /// </summary>
    public sealed class RWTexture2D<T>
    {
        public T this[int2 coord]
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public T this[uint2 coord]
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public T this[int x, int y]
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public T this[uint x, uint y]
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Read-only 3D texture. Translates to HLSL <c>Texture3D&lt;T&gt;</c>.
    /// </summary>
    public sealed class Texture3D<T>
    {
        public T this[int3 coord] => throw new NotSupportedException();
        public T this[uint3 coord] => throw new NotSupportedException();
    }

    /// <summary>
    /// Read-write 3D texture. Translates to HLSL <c>RWTexture3D&lt;T&gt;</c>.
    /// </summary>
    public sealed class RWTexture3D<T>
    {
        public T this[int3 coord]
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
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
        public T this[int index] => throw new NotSupportedException();
        public T this[uint index] => throw new NotSupportedException();
    }

    /// <summary>
    /// Read-write typed buffer. Translates to HLSL <c>RWBuffer&lt;T&gt;</c>.
    /// </summary>
    public sealed class RWBuffer<T>
    {
        public T this[int index]
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public T this[uint index]
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Read-only structured buffer of arbitrary structs. Translates to HLSL
    /// <c>StructuredBuffer&lt;T&gt;</c>.
    /// </summary>
    public sealed class StructuredBuffer<T>
    {
        public T this[int index] => throw new NotSupportedException();
        public T this[uint index] => throw new NotSupportedException();
        public int Length => throw new NotSupportedException();
    }

    /// <summary>
    /// Read-write structured buffer of arbitrary structs. Translates to HLSL
    /// <c>RWStructuredBuffer&lt;T&gt;</c>.
    /// </summary>
    public sealed class RWStructuredBuffer<T>
    {
        public T this[int index]
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public T this[uint index]
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public int Length => throw new NotSupportedException();
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

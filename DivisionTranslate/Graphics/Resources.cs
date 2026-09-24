//
// Copyright (c) 2026 Rex Woodfield and DivisionTranslate contributors
//
// This file is part of DivisionTranslate and is subject to the terms
// of the DivisionTranslate License. See the LICENSE.txt file in the
// project root for full license terms.
//
using DivisionEngine.MathLib;

namespace DivisionEngine.Graphics
{
    // NOTE: These are compile-only markers. Their bodies never execute -
    // Roslyn walks the shader struct's syntax tree to translate it to HLSL,
    // and then we bind real D3D11 resources by field name at dispatch time.
    // The indexers exist only so the C# in the shader struct compiles cleanly.

    public sealed class Texture2D<T>
    {
        public T this[int2 coord] => throw new NotSupportedException();
        public T this[uint2 coord] => throw new NotSupportedException();
        public T this[int x, int y] => throw new NotSupportedException();
        public T this[uint x, uint y] => throw new NotSupportedException();
    }

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

    public sealed class Texture3D<T>
    {
        public T this[int3 coord] => throw new NotSupportedException();
        public T this[uint3 coord] => throw new NotSupportedException();
    }

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

    public sealed class Buffer<T>
    {
        public T this[int index] => throw new NotSupportedException();
        public T this[uint index] => throw new NotSupportedException();
    }

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

    public sealed class StructuredBuffer<T>
    {
        public T this[int index] => throw new NotSupportedException();
        public T this[uint index] => throw new NotSupportedException();
        public int Length => throw new NotSupportedException();
    }

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

    public sealed class ConstantBuffer<T> { }
    public sealed class Sampler { }
}

//
// Copyright (c) 2026 Rex Woodfield and DivisionTranslate contributors
//
// This file is part of DivisionTranslate and is subject to the terms
// of the DivisionTranslate License. See the LICENSE.txt file in the
// project root for full license terms.
//
using DivisionEngine.MathLib;
using Vortice.DXGI;

namespace DivisionEngine.Graphics
{
    /// <summary>
    /// Maps DivisionMath CPU types to the matching typed DXGI format. Only used for
    /// natively-formatted resources (<c>Buffer</c>/<c>RWBuffer</c>,
    /// <c>Texture2D</c>/<c>RWTexture2D</c>) - arbitrary structs like
    /// <c>SDFObjectDTO</c> have no typed format and must go through
    /// <c>StructuredBuffer</c>/<c>RWStructuredBuffer</c> instead.
    /// </summary>
    public static class DXGIFormatMapper
    {
        /// <summary>
        /// Gets the typed DXGI format for <typeparamref name="T"/>.
        /// </summary>
        /// <typeparam name="T">A DivisionMath scalar or vector type.</typeparam>
        /// <returns>The matching <see cref="Format"/>.</returns>
        /// <exception cref="NotSupportedException">
        /// If <typeparamref name="T"/> has no typed DXGI equivalent.
        /// </exception>
        public static Format GetFormat<T>() where T : unmanaged => GetFormat(typeof(T));

        /// <summary>
        /// Gets the typed DXGI format for <paramref name="type"/>.
        /// </summary>
        /// <param name="type">A DivisionMath scalar or vector type.</param>
        /// <returns>The matching <see cref="Format"/>.</returns>
        /// <exception cref="NotSupportedException">
        /// If <paramref name="type"/> has no typed DXGI equivalent.
        /// </exception>
        public static Format GetFormat(Type type)
        {
            if (type == typeof(float)) return Format.R32_Float;
            if (type == typeof(float2)) return Format.R32G32_Float;
            if (type == typeof(float3)) return Format.R32G32B32_Float;
            if (type == typeof(float4)) return Format.R32G32B32A32_Float;

            if (type == typeof(int)) return Format.R32_SInt;
            if (type == typeof(int2)) return Format.R32G32_SInt;
            if (type == typeof(int3)) return Format.R32G32B32_SInt;
            if (type == typeof(int4)) return Format.R32G32B32A32_SInt;

            if (type == typeof(uint)) return Format.R32_UInt;
            if (type == typeof(uint2)) return Format.R32G32_UInt;
            if (type == typeof(uint3)) return Format.R32G32B32_UInt;
            if (type == typeof(uint4)) return Format.R32G32B32A32_UInt;

            throw new NotSupportedException(
                $"'{type.Name}' has no typed DXGI format. Use StructuredBuffer<{type.Name}>/RWStructuredBuffer<{type.Name}> for custom structs.");
        }
    }
}

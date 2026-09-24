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
    /// <summary>
    /// Extension functions for Texture2D objects in HLSL only.
    /// </summary>
    public static class Texture2DExtensions
    {
        /// <summary>
        /// Bilinear sample at normalized UVs. 
        /// </summary>
        /// <remarks>
        /// <c>tex.Sample(s, uv)</c>
        /// </remarks>
        public static T Sample<T>(this Texture2D<T> texture, float2 uv)
            => throw new NotSupportedException();

        /// <summary>
        /// Sample at an explicit mip. 
        /// </summary>
        /// <remarks>
        /// <c>tex.SampleLevel(s, uv, mip)</c>
        /// </remarks>
        public static T SampleLevel<T>(this Texture2D<T> texture, float2 uv, float mipLevel)
            => throw new NotSupportedException();

        /// <summary>
        /// Unfiltered texel fetch.
        /// </summary>
        /// <remarks>
        /// <c>tex.Load(int3(coord, mip))</c>
        /// </remarks>
        public static T Load<T>(this Texture2D<T> texture, int2 coord)
            => throw new NotSupportedException();

        /// <summary>
        /// Unfiltered texel fetch with explicit mip.
        /// </summary>
        /// <remarks>
        /// <c>tex.Load(int3(coord, mip, miplevel))</c>
        /// </remarks>
        public static T Load<T>(this Texture2D<T> texture, int2 coord, int mipLevel)
            => throw new NotSupportedException();
    }
}

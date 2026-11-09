//
// Copyright (c) 2026 Rex Woodfield and DivisionCompute contributors
//
// This file is part of DivisionCompute and is subject to the terms
// of the DivisionCompute License. See the LICENSE.txt file in the
// project root for full license terms.
//
using System.Runtime.CompilerServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DivisionEngine.Graphics
{
    /// <summary>
    /// Shared allocation, upload, and readback plumbing for <see cref="Texture3D{T}"/>
    /// and <see cref="RWTexture3D{T}"/>.
    /// </summary>
    internal static class GpuTexture3DHelpers
    {
        public static Format GetFormat<T>() where T : unmanaged
            => DXGIFormatMapper.GetFormat<T>();

        public static int GetElementSize<T>() where T : unmanaged
            => Unsafe.SizeOf<T>();

        public static void ValidateSize(int width, int height, int depth)
        {
            if (width <= 0 || width > ID3D11Resource.MaximumTexture3DSize)
                throw new ArgumentOutOfRangeException(
                    nameof(width), width, $"Texture width must be between 1 and {ID3D11Resource.MaximumTexture3DSize}.");

            if (height <= 0 || height > ID3D11Resource.MaximumTexture3DSize)
                throw new ArgumentOutOfRangeException(
                    nameof(height), height, $"Texture height must be between 1 and {ID3D11Resource.MaximumTexture3DSize}.");

            if (depth <= 0 || depth > ID3D11Resource.MaximumTexture3DSize)
                throw new ArgumentOutOfRangeException(
                    nameof(depth), depth, $"Texture depth must be between 1 and {ID3D11Resource.MaximumTexture3DSize}.");

            if ((long)width * height * depth > int.MaxValue)
                throw new ArgumentOutOfRangeException(
                    nameof(width), width, "The texture has too many voxels for a single CPU array.");
        }

        public static ID3D11Texture3D CreateTexture(
            ID3D11Device device,
            int width,
            int height,
            int depth,
            Format format,
            bool writable)
        {
            var description = new Texture3DDescription(
                format,
                (uint)width,
                (uint)height,
                (uint)depth,
                mipLevels: 1,
                bindFlags: writable
                    ? BindFlags.UnorderedAccess | BindFlags.ShaderResource
                    : BindFlags.ShaderResource,
                usage: ResourceUsage.Default,
                cpuAccessFlags: CpuAccessFlags.None,
                miscFlags: ResourceOptionFlags.None);

            return device.CreateTexture3D(description);
        }

        public static ID3D11ShaderResourceView CreateSrv(
            ID3D11Device device,
            ID3D11Texture3D texture,
            Format format)
            => device.CreateShaderResourceView(texture, new ShaderResourceViewDescription
            {
                Format = format,
                ViewDimension = ShaderResourceViewDimension.Texture3D,
                Texture3D = new Texture3DShaderResourceView
                {
                    MipLevels = 1,
                    MostDetailedMip = 0,
                },
            });

        public static ID3D11UnorderedAccessView CreateUav(
            ID3D11Device device,
            ID3D11Texture3D texture,
            Format format,
            int depth)
            => device.CreateUnorderedAccessView(texture, new UnorderedAccessViewDescription
            {
                Format = format,
                ViewDimension = UnorderedAccessViewDimension.Texture3D,
                Texture3D = new Texture3DUnorderedAccessView
                {
                    MipSlice = 0,
                    FirstWSlice = 0,
                    WSize = (uint)depth,
                },
            });

        public static T[] Read<T>(
            ID3D11Device device,
            ID3D11DeviceContext context,
            ID3D11Texture3D texture,
            int width,
            int height,
            int depth,
            Format format) where T : unmanaged
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(texture);
            ValidateSize(width, height, depth);

            var stagingDescription = new Texture3DDescription(
                format,
                (uint)width,
                (uint)height,
                (uint)depth,
                mipLevels: 1,
                bindFlags: BindFlags.None,
                usage: ResourceUsage.Staging,
                cpuAccessFlags: CpuAccessFlags.Read,
                miscFlags: ResourceOptionFlags.None);

            using ID3D11Texture3D staging = device.CreateTexture3D(stagingDescription);
            context.CopyResource(staging, texture);

            MappedSubresource mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int elementSize = GetElementSize<T>();
                int rowBytes = checked(width * elementSize);
                var result = new T[checked(width * height * depth)];

                unsafe
                {
                    byte* source = (byte*)mapped.DataPointer;
                    for (int z = 0; z < depth; z++)
                    {
                        byte* sourceSlice = source + (long)z * mapped.DepthPitch;
                        int sliceOffset = z * height * width;

                        for (int y = 0; y < height; y++)
                        {
                            fixed (T* destination = &result[sliceOffset + y * width])
                            {
                                System.Buffer.MemoryCopy(
                                    sourceSlice + (long)y * mapped.RowPitch,
                                    destination,
                                    rowBytes,
                                    rowBytes);
                            }
                        }
                    }
                }

                return result;
            }
            finally
            {
                context.Unmap(staging, 0);
            }
        }

        public static void Write<T>(
            ID3D11DeviceContext context,
            ID3D11Texture3D texture,
            int width,
            int height,
            int depth,
            ReadOnlySpan<T> data) where T : unmanaged
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(texture);
            ValidateSize(width, height, depth);

            int expected = checked(width * height * depth);
            if (data.Length != expected)
                throw new ArgumentException(
                    $"The source contains {data.Length} voxels but the texture requires {expected}.",
                    nameof(data));

            int rowBytes = checked(width * GetElementSize<T>());
            uint depthBytes = checked((uint)((long)rowBytes * height));
            context.UpdateSubresource(data, texture, 0, (uint)rowBytes, depthBytes);
        }
    }
}

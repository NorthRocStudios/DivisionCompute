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
    /// Shared allocation, upload, and readback plumbing for <see cref="Texture2D{T}"/>
    /// and <see cref="RWTexture2D{T}"/>.
    /// </summary>
    internal static class GpuTexture2DHelpers
    {
        public static Format GetFormat<T>() where T : unmanaged
            => DXGIFormatMapper.GetFormat<T>();

        public static int GetElementSize<T>() where T : unmanaged
            => Unsafe.SizeOf<T>();

        public static void ValidateSize(int width, int height)
        {
            if (width <= 0 || width > ID3D11Resource.MaximumTexture2DSize)
                throw new ArgumentOutOfRangeException(
                    nameof(width), width, $"Texture width must be between 1 and {ID3D11Resource.MaximumTexture2DSize}.");

            if (height <= 0 || height > ID3D11Resource.MaximumTexture2DSize)
                throw new ArgumentOutOfRangeException(
                    nameof(height), height, $"Texture height must be between 1 and {ID3D11Resource.MaximumTexture2DSize}.");

            if ((long)width * height > int.MaxValue)
                throw new ArgumentOutOfRangeException(
                    nameof(width), width, "The texture has too many texels for a single CPU array.");
        }

        public static ID3D11Texture2D CreateTexture(
            ID3D11Device device,
            int width,
            int height,
            Format format,
            bool writable)
        {
            var description = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = writable
                    ? BindFlags.UnorderedAccess | BindFlags.ShaderResource
                    : BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            };

            return device.CreateTexture2D(description);
        }

        public static ID3D11ShaderResourceView CreateSrv(
            ID3D11Device device,
            ID3D11Texture2D texture,
            Format format)
            => device.CreateShaderResourceView(texture, new ShaderResourceViewDescription
            {
                Format = format,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new Texture2DShaderResourceView
                {
                    MipLevels = 1,
                    MostDetailedMip = 0,
                },
            });

        public static ID3D11UnorderedAccessView CreateUav(
            ID3D11Device device,
            ID3D11Texture2D texture,
            Format format)
            => device.CreateUnorderedAccessView(texture, new UnorderedAccessViewDescription
            {
                Format = format,
                ViewDimension = UnorderedAccessViewDimension.Texture2D,
                Texture2D = new Texture2DUnorderedAccessView
                {
                    MipSlice = 0,
                },
            });

        public static T[] Read<T>(
            ID3D11Device device,
            ID3D11DeviceContext context,
            ID3D11Texture2D texture,
            int width,
            int height,
            Format format) where T : unmanaged
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(texture);
            ValidateSize(width, height);

            var stagingDescription = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            };

            using ID3D11Texture2D staging = device.CreateTexture2D(stagingDescription);
            context.CopyResource(staging, texture);

            MappedSubresource mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int elementSize = GetElementSize<T>();
                int rowBytes = checked(width * elementSize);
                var result = new T[checked(width * height)];

                unsafe
                {
                    byte* source = (byte*)mapped.DataPointer;
                    for (int y = 0; y < height; y++)
                    {
                        fixed (T* destination = &result[y * width])
                        {
                            System.Buffer.MemoryCopy(
                                source + (long)y * mapped.RowPitch,
                                destination,
                                rowBytes,
                                rowBytes);
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
            ID3D11Texture2D texture,
            int width,
            int height,
            ReadOnlySpan<T> data) where T : unmanaged
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(texture);
            ValidateSize(width, height);

            int expected = checked(width * height);
            if (data.Length != expected)
                throw new ArgumentException(
                    $"The source contains {data.Length} texels but the texture requires {expected}.",
                    nameof(data));

            int rowBytes = checked(width * GetElementSize<T>());
            uint depthBytes = checked((uint)((long)rowBytes * height));
            context.UpdateSubresource(data, texture, 0, (uint)rowBytes, depthBytes);
        }
    }
}

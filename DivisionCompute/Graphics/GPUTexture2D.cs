//
// Copyright (c) 2026 Rex Woodfield and DivisionCompute contributors
//
// This file is part of DivisionCompute and is subject to the terms
// of the DivisionCompute License. See the LICENSE.txt file in the
// project root for full license terms.
//
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DivisionEngine.Graphics
{
    /// <summary>
    /// Owns a D3D11 texture plus its UAV (write) and SRV (read) views. This is the
    /// runtime counterpart to the <see cref="RWTexture2D{T}"/> compile-time marker.
    /// </summary>
    /// <typeparam name="T">
    /// The pixel element type. Must have a matching DXGI format - see
    /// <see cref="DXGIFormatMapper"/>. Typically <c>float4</c>.
    /// </typeparam>
    public sealed class GPUTexture2D<T> : IDisposable where T : unmanaged
    {
        /// <summary>
        /// The underlying D3D11 texture resource.
        /// </summary>
        public ID3D11Texture2D Texture { get; }

        /// <summary>
        /// Unordered-access view used by compute shaders that write to this texture.
        /// </summary>
        public ID3D11UnorderedAccessView Uav { get; }

        /// <summary>
        /// Shader-resource view used by shaders that sample this texture.
        /// </summary>
        public ID3D11ShaderResourceView Srv { get; }

        /// <summary>
        /// Width in pixels.
        /// </summary>
        public int Width { get; }

        /// <summary>
        /// Height in pixels.
        /// </summary>
        public int Height { get; }

        /// <summary>
        /// The DXGI format of the texture and its views.
        /// </summary>
        public Format Format { get; }

        /// <summary>
        /// Creates a single-mip, single-array-slice texture with default usage and
        /// both UAV and SRV bind flags.
        /// </summary>
        /// <param name="device">The D3D11 device to allocate on.</param>
        /// <param name="width">Width in pixels.</param>
        /// <param name="height">Height in pixels.</param>
        /// <param name="format">DXGI pixel format; must match <typeparamref name="T"/>.</param>
        public GPUTexture2D(ID3D11Device device, int width, int height, Format format)
        {
            Width = width;
            Height = height;
            Format = format;

            var desc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            };

            Texture = device.CreateTexture2D(desc);

            Uav = device.CreateUnorderedAccessView(Texture, new UnorderedAccessViewDescription
            {
                Format = format,
                ViewDimension = UnorderedAccessViewDimension.Texture2D,
                Texture2D = new Texture2DUnorderedAccessView { MipSlice = 0 },
            });

            Srv = device.CreateShaderResourceView(Texture, new ShaderResourceViewDescription
            {
                Format = format,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
            });
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Srv?.Dispose();
            Uav?.Dispose();
            Texture?.Dispose();
        }
    }
}

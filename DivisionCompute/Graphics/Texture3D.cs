//
// Copyright (c) 2026 Rex Woodfield and DivisionCompute contributors
//
// This file is part of DivisionCompute and is subject to the terms
// of the DivisionCompute License. See the LICENSE.txt file in the
// project root for full license terms.
//
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DivisionEngine.Graphics
{
    /// <summary>
    /// CPU-side half of <see cref="Texture3D{T}"/>: owns a read-only D3D11 3D
    /// texture and its shader-resource view.
    /// </summary>
    public partial class Texture3D<T> : IDisposable where T : unmanaged
    {
        private readonly ID3D11Device _device;

        /// <summary>
        /// The underlying D3D11 texture resource.
        /// </summary>
        public ID3D11Texture3D Texture { get; }

        /// <summary>
        /// The shader-resource view used when binding this texture to a shader.
        /// </summary>
        public ID3D11ShaderResourceView Srv { get; }

        /// <summary>
        /// Width in voxels.
        /// </summary>
        public int Width { get; }

        /// <summary>
        /// Height in voxels.
        /// </summary>
        public int Height { get; }

        /// <summary>
        /// Depth in voxels.
        /// </summary>
        public int Depth { get; }

        /// <summary>
        /// The DXGI format of the texture and its view.
        /// </summary>
        public Format Format { get; }

        /// <summary>
        /// Size of one voxel in bytes.
        /// </summary>
        public int ElementSize { get; }

        /// <summary>
        /// Total texture size in bytes.
        /// </summary>
        public long SizeInBytes => (long)Width * Height * Depth * ElementSize;

        /// <summary>
        /// Creates a read-only 3D texture using the DXGI format that matches
        /// <typeparamref name="T"/>.
        /// </summary>
        public Texture3D(ID3D11Device device, int width, int height, int depth)
            : this(device, width, height, depth, GpuTexture3DHelpers.GetFormat<T>())
        {
        }

        /// <summary>
        /// Creates a read-only 3D texture with an explicit DXGI format.
        /// </summary>
        public Texture3D(ID3D11Device device, int width, int height, int depth, Format format)
        {
            ArgumentNullException.ThrowIfNull(device);
            if (format == Format.Unknown)
                throw new ArgumentException("A texture format is required.", nameof(format));
            GpuTexture3DHelpers.ValidateSize(width, height, depth);

            _device = device;
            Width = width;
            Height = height;
            Depth = depth;
            Format = format;
            ElementSize = GpuTexture3DHelpers.GetElementSize<T>();
            Texture = GpuTexture3DHelpers.CreateTexture(device, width, height, depth, format, writable: false);
            Srv = GpuTexture3DHelpers.CreateSrv(device, Texture, format);
        }

        /// <summary>
        /// Copies the current GPU contents into a new CPU array in
        /// <c>x + y * Width + z * Width * Height</c> order.
        /// </summary>
        public T[] GetData(ID3D11DeviceContext context)
            => GpuTexture3DHelpers.Read<T>(_device, context, Texture, Width, Height, Depth, Format);

        /// <summary>
        /// Uploads a complete CPU volume into the texture.
        /// </summary>
        public void SetData(ID3D11DeviceContext context, ReadOnlySpan<T> data)
            => GpuTexture3DHelpers.Write(context, Texture, Width, Height, Depth, data);

        /// <inheritdoc/>
        public void Dispose()
        {
            Srv?.Dispose();
            Texture?.Dispose();
        }
    }
}

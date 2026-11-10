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
    /// CPU-side half of <see cref="Texture2D{T}"/>: owns a read-only D3D11 texture
    /// and its shader-resource view.
    /// </summary>
    public partial class Texture2D<T> : IDisposable where T : unmanaged
    {
        private readonly ID3D11Device _device;

        /// <summary>
        /// The underlying D3D11 texture resource.
        /// </summary>
        public ID3D11Texture2D Texture { get; }

        /// <summary>
        /// The shader-resource view used when binding this texture to a shader.
        /// </summary>
        public ID3D11ShaderResourceView Srv { get; }

        /// <summary>
        /// Width in texels.
        /// </summary>
        public int Width { get; }

        /// <summary>
        /// Height in texels.
        /// </summary>
        public int Height { get; }

        /// <summary>
        /// The DXGI format of the texture and its view.
        /// </summary>
        public Format Format { get; }

        /// <summary>
        /// Size of one texel in bytes.
        /// </summary>
        public int ElementSize { get; }

        /// <summary>
        /// Total texture size in bytes.
        /// </summary>
        public long SizeInBytes => (long)Width * Height * ElementSize;

        /// <summary>
        /// Creates a read-only texture using the DXGI format that matches
        /// <typeparamref name="T"/>.
        /// </summary>
        public Texture2D(ID3D11Device device, int width, int height)
            : this(device, width, height, GpuTexture2DHelpers.GetFormat<T>())
        {
        }

        /// <summary>
        /// Creates a read-only texture with an explicit DXGI format.
        /// </summary>
        public Texture2D(ID3D11Device device, int width, int height, Format format)
        {
            ArgumentNullException.ThrowIfNull(device);
            if (format == Format.Unknown)
                throw new ArgumentException("A texture format is required.", nameof(format));
            GpuTexture2DHelpers.ValidateSize(width, height);

            _device = device;
            Width = width;
            Height = height;
            Format = format;
            ElementSize = GpuTexture2DHelpers.GetElementSize<T>();
            Texture = GpuTexture2DHelpers.CreateTexture(device, width, height, format, writable: false);
            Srv = GpuTexture2DHelpers.CreateSrv(device, Texture, format);
        }

        /// <summary>
        /// Copies the current GPU contents into a new CPU array in row-major,
        /// top-left-origin order.
        /// </summary>
        public T[] GetData(ID3D11DeviceContext context)
            => GpuTexture2DHelpers.Read<T>(_device, context, Texture, Width, Height, Format);

        /// <summary>
        /// Copies the current GPU contents into an existing CPU array. Reuse this
        /// overload for per-frame readbacks.
        /// </summary>
        public void GetData(ID3D11DeviceContext context, Span<T> destination)
            => GpuTexture2DHelpers.Read<T>(_device, context, Texture, Width, Height, Format, destination);

        /// <summary>
        /// Uploads a complete CPU image into the texture.
        /// </summary>
        public void SetData(ID3D11DeviceContext context, ReadOnlySpan<T> data)
            => GpuTexture2DHelpers.Write(context, Texture, Width, Height, data);

        /// <inheritdoc/>
        /// <summary>
        /// Copies this texture into <paramref name="destination"/> on the GPU.
        /// </summary>
        /// <param name="context">The immediate context used for the copy.</param>
        /// <param name="destination">The texture to copy into. Must match size and format.</param>
        public void CopyTo(ID3D11DeviceContext context, Texture2D<T> destination)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(destination);
            GpuTexture2DHelpers.ValidateCopy(Width, Height, Format, destination.Width, destination.Height, destination.Format);
            context.CopyResource(destination.Texture, Texture);
        }

        /// <summary>
        /// Releases the texture and its view.
        /// </summary>
        public void Dispose()
        {
            Srv?.Dispose();
            Texture?.Dispose();
        }
    }
}


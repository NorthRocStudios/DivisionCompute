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
    /// Owns a D3D11 structured buffer that is bound to a shader as a read-only
    /// <c>StructuredBuffer&lt;T&gt;</c>.
    /// </summary>
    /// <typeparam name="T">
    /// An unmanaged, sequential struct that matches the HLSL element struct.
    /// </typeparam>
    /// <remarks>
    /// Create instances through <see cref="DivisionComputeContext.CreateReadOnlyBuffer{T}(ReadOnlySpan{T})"/>.
    /// The wrapper owns the buffer and its shader-resource view; dispose it after
    /// the shader has finished using it.
    /// </remarks>
    public sealed class ReadOnlyBuffer<T> : IDisposable where T : unmanaged
    {
        private readonly ID3D11Device _device;

        /// <summary>
        /// The underlying D3D11 buffer resource.
        /// </summary>
        public ID3D11Buffer Buffer { get; }

        /// <summary>
        /// The shader-resource view used when binding this buffer to a shader.
        /// </summary>
        public ID3D11ShaderResourceView Srv { get; }

        /// <summary>
        /// Number of elements in the buffer.
        /// </summary>
        public int Count { get; }

        /// <summary>
        /// Size of one element in bytes.
        /// </summary>
        public int Stride { get; }

        /// <summary>
        /// Total buffer size in bytes.
        /// </summary>
        public long SizeInBytes => (long)Count * Stride;

        /// <summary>
        /// Creates a read-only structured buffer and uploads <paramref name="data"/>.
        /// </summary>
        /// <param name="device">The D3D11 device that owns the buffer.</param>
        /// <param name="data">The initial element data. Must not be empty.</param>
        /// <exception cref="NotSupportedException">
        /// If <typeparamref name="T"/> has a layout that cannot be represented safely
        /// in an HLSL structured buffer.
        /// </exception>
        public ReadOnlyBuffer(ID3D11Device device, ReadOnlySpan<T> data)
        {
            ArgumentNullException.ThrowIfNull(device);

            Stride = GpuBufferHelpers.GetStride<T>();
            Count = data.Length;
            GpuBufferHelpers.ValidateCount(Count, Stride);

            _device = device;
            Buffer = device.CreateBuffer(
                data,
                BindFlags.ShaderResource,
                ResourceUsage.Default,
                CpuAccessFlags.None,
                ResourceOptionFlags.BufferStructured,
                sizeInBytes: checked((uint)SizeInBytes),
                structureByteStride: (uint)Stride);

            Srv = device.CreateShaderResourceView(
                Buffer,
                new ShaderResourceViewDescription(Buffer, Format.Unknown, 0, (uint)Count));
        }

        /// <summary>
        /// Copies the current GPU contents into a new CPU array.
        /// </summary>
        /// <param name="context">The immediate context used for the copy and map.</param>
        /// <remarks>This blocks until the GPU has finished prior work.</remarks>
        public T[] GetData(ID3D11DeviceContext context)
            => GpuBufferHelpers.Read<T>(_device, context, Buffer, Count, Stride);

        /// <summary>
        /// Uploads new contents into the buffer. The entire buffer is replaced when
        /// <paramref name="data"/> has <see cref="Count"/> elements; a shorter span
        /// updates only the leading elements.
        /// </summary>
        /// <param name="context">The immediate context used for the upload.</param>
        /// <param name="data">The new element data.</param>
        public void SetData(ID3D11DeviceContext context, ReadOnlySpan<T> data)
            => GpuBufferHelpers.Write(context, Buffer, data, Count);

        /// <inheritdoc/>
        public void Dispose()
        {
            Srv?.Dispose();
            Buffer?.Dispose();
        }
    }
}

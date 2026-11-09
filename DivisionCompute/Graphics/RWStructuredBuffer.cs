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
    /// CPU-side half of <see cref="RWStructuredBuffer{T}"/>: owns a D3D11
    /// structured buffer plus UAV and SRV views.
    /// </summary>
    public sealed partial class RWStructuredBuffer<T> : IDisposable where T : unmanaged
    {
        private readonly ID3D11Device _device;
        private readonly int _count;

        /// <summary>
        /// The underlying D3D11 buffer resource.
        /// </summary>
        public ID3D11Buffer Buffer { get; }

        /// <summary>
        /// The unordered-access view used when writing to this buffer.
        /// </summary>
        public ID3D11UnorderedAccessView Uav { get; }

        /// <summary>
        /// The shader-resource view used when reading this buffer in a later pass.
        /// </summary>
        public ID3D11ShaderResourceView Srv { get; }

        /// <summary>
        /// Number of elements in the buffer.
        /// </summary>
        public int Count => _count;

        /// <summary>
        /// Number of elements in the buffer. Shader code also sees this as a
        /// <c>GetDimensions</c> helper.
        /// </summary>
        public int Length => _count;

        /// <summary>
        /// Size of one element in bytes.
        /// </summary>
        public int Stride { get; }

        /// <summary>
        /// Total buffer size in bytes.
        /// </summary>
        public long SizeInBytes => (long)Count * Stride;

        /// <summary>
        /// Creates a zero-initialized read-write structured buffer.
        /// </summary>
        /// <param name="device">The D3D11 device that owns the buffer.</param>
        /// <param name="count">Number of elements; must be positive.</param>
        public RWStructuredBuffer(ID3D11Device device, int count)
        {
            ArgumentNullException.ThrowIfNull(device);

            Stride = GpuBufferHelpers.GetStride<T>();
            GpuBufferHelpers.ValidateCount(count, Stride);
            _count = count;

            _device = device;
            T[] initialData = new T[count];
            Buffer = CreateBuffer(device, initialData);
            Uav = device.CreateUnorderedAccessView(
                Buffer,
                new UnorderedAccessViewDescription(Buffer, Format.Unknown, 0, (uint)Count));
            Srv = device.CreateShaderResourceView(
                Buffer,
                new ShaderResourceViewDescription(Buffer, Format.Unknown, 0, (uint)Count));
        }

        /// <summary>
        /// Creates a read-write structured buffer and uploads <paramref name="data"/>.
        /// </summary>
        /// <param name="device">The D3D11 device that owns the buffer.</param>
        /// <param name="data">The initial element data. Must not be empty.</param>
        public RWStructuredBuffer(ID3D11Device device, ReadOnlySpan<T> data)
        {
            ArgumentNullException.ThrowIfNull(device);

            Stride = GpuBufferHelpers.GetStride<T>();
            _count = data.Length;
            GpuBufferHelpers.ValidateCount(_count, Stride);

            _device = device;
            Buffer = CreateBuffer(device, data);
            Uav = device.CreateUnorderedAccessView(
                Buffer,
                new UnorderedAccessViewDescription(Buffer, Format.Unknown, 0, (uint)Count));
            Srv = device.CreateShaderResourceView(
                Buffer,
                new ShaderResourceViewDescription(Buffer, Format.Unknown, 0, (uint)Count));
        }

        /// <summary>
        /// Copies the current GPU contents into a new CPU array.
        /// </summary>
        /// <param name="context">The immediate context used for the copy and map.</param>
        public T[] GetData(ID3D11DeviceContext context)
            => GpuBufferHelpers.Read<T>(_device, context, Buffer, Count, Stride);

        /// <summary>
        /// Uploads new contents into the buffer. A shorter span updates only the
        /// leading elements.
        /// </summary>
        /// <param name="context">The immediate context used for the upload.</param>
        /// <param name="data">The new element data.</param>
        public void SetData(ID3D11DeviceContext context, ReadOnlySpan<T> data)
            => GpuBufferHelpers.Write(context, Buffer, data, Count);

        /// <inheritdoc/>
        public void Dispose()
        {
            Srv?.Dispose();
            Uav?.Dispose();
            Buffer?.Dispose();
        }

        private ID3D11Buffer CreateBuffer(ID3D11Device device, ReadOnlySpan<T> data)
            => device.CreateBuffer(
                data,
                BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                ResourceUsage.Default,
                CpuAccessFlags.None,
                ResourceOptionFlags.BufferStructured,
                sizeInBytes: checked((uint)SizeInBytes),
                structureByteStride: (uint)Stride);
    }
}

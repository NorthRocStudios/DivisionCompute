//
// Copyright (c) 2026 Rex Woodfield and DivisionCompute contributors
//
// This file is part of DivisionCompute and is subject to the terms
// of the DivisionCompute License. See the LICENSE.txt file in the project root
// for full license terms.
//
using Vortice.Direct3D11;

namespace DivisionEngine.Graphics
{
    /// <summary>
    /// CPU-side half of <see cref="Buffer{T}"/>: owns a typed D3D11 buffer and its
    /// shader-resource view. Translates to HLSL <c>Buffer&lt;T&gt;</c> in shader code.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="StructuredBuffer{T}"/>, a typed buffer uses a DXGI format, so
    /// <typeparamref name="T"/> must be a scalar or vector with a matching format
    /// (<c>float</c>, <c>int</c>, <c>uint</c>, and their 2/3/4 component vectors).
    /// </remarks>
    public sealed partial class Buffer<T> : IDisposable where T : unmanaged
    {
        private readonly ID3D11Device _device;
        private readonly int _count;

        /// <summary>
        /// The underlying D3D11 buffer resource.
        /// </summary>
        public ID3D11Buffer Resource { get; }

        /// <summary>
        /// The shader-resource view used when binding this buffer to a shader.
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
        public int ElementSize { get; }

        /// <summary>
        /// Total buffer size in bytes.
        /// </summary>
        public long SizeInBytes => (long)Count * ElementSize;

        /// <summary>
        /// Creates a typed buffer and uploads <paramref name="data"/>.
        /// </summary>
        /// <param name="device">The D3D11 device that owns the buffer.</param>
        /// <param name="data">The initial element data. Must not be empty.</param>
        public Buffer(ID3D11Device device, ReadOnlySpan<T> data)
        {
            ArgumentNullException.ThrowIfNull(device);
            if (data.IsEmpty)
                throw new ArgumentException("A GPU buffer must contain at least one element.", nameof(data));

            _device = device;
            _count = data.Length;
            ElementSize = GpuBufferHelpers.GetTypedElementSize<T>();
            Resource = device.CreateBuffer(
                data,
                BindFlags.ShaderResource,
                ResourceUsage.Default,
                CpuAccessFlags.None,
                ResourceOptionFlags.None,
                sizeInBytes: checked((uint)SizeInBytes),
                structureByteStride: 0);

            Srv = device.CreateShaderResourceView(
                Resource,
                new ShaderResourceViewDescription(Resource, DXGIFormatMapper.GetFormat<T>(), 0, (uint)Count));
        }

        /// <summary>
        /// Copies the current GPU contents into a new CPU array.
        /// </summary>
        public T[] GetData(ID3D11DeviceContext context)
            => GpuBufferHelpers.Read<T>(_device, context, Resource, Count, ElementSize);

        /// <summary>
        /// Copies the current GPU contents into an existing CPU array.
        /// </summary>
        public void GetData(ID3D11DeviceContext context, Span<T> destination)
            => GpuBufferHelpers.Read<T>(_device, context, Resource, Count, ElementSize, destination);

        /// <inheritdoc/>
        public void Dispose()
        {
            Srv?.Dispose();
            Resource?.Dispose();
        }
    }

    /// <summary>
    /// CPU-side half of <see cref="RWBuffer{T}"/>: owns a typed D3D11 buffer plus UAV
    /// and SRV views. Translates to HLSL <c>RWBuffer&lt;T&gt;</c> in shader code.
    /// </summary>
    public sealed partial class RWBuffer<T> : IDisposable where T : unmanaged
    {
        private readonly ID3D11Device _device;
        private readonly int _count;

        /// <summary>
        /// The underlying D3D11 buffer resource.
        /// </summary>
        public ID3D11Buffer Resource { get; }

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
        public int ElementSize { get; }

        /// <summary>
        /// Total buffer size in bytes.
        /// </summary>
        public long SizeInBytes => (long)Count * ElementSize;

        /// <summary>
        /// Creates a zero-initialized read-write typed buffer.
        /// </summary>
        /// <param name="device">The D3D11 device that owns the buffer.</param>
        /// <param name="count">Number of elements; must be positive.</param>
        public RWBuffer(ID3D11Device device, int count)
            : this(device, new T[Math.Max(1, count)])
        {
        }

        /// <summary>
        /// Creates a read-write typed buffer and uploads <paramref name="data"/>.
        /// </summary>
        /// <param name="device">The D3D11 device that owns the buffer.</param>
        /// <param name="data">The initial element data. Must not be empty.</param>
        public RWBuffer(ID3D11Device device, ReadOnlySpan<T> data)
        {
            ArgumentNullException.ThrowIfNull(device);
            if (data.IsEmpty)
                throw new ArgumentException("A GPU buffer must contain at least one element.", nameof(data));

            _device = device;
            _count = data.Length;
            ElementSize = GpuBufferHelpers.GetTypedElementSize<T>();
            Resource = device.CreateBuffer(
                data,
                BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                ResourceUsage.Default,
                CpuAccessFlags.None,
                ResourceOptionFlags.None,
                sizeInBytes: checked((uint)SizeInBytes),
                structureByteStride: 0);

            Uav = device.CreateUnorderedAccessView(
                Resource,
                new UnorderedAccessViewDescription(Resource, DXGIFormatMapper.GetFormat<T>(), 0, (uint)Count));
            Srv = device.CreateShaderResourceView(
                Resource,
                new ShaderResourceViewDescription(Resource, DXGIFormatMapper.GetFormat<T>(), 0, (uint)Count));
        }

        /// <summary>
        /// Copies the current GPU contents into a new CPU array.
        /// </summary>
        public T[] GetData(ID3D11DeviceContext context)
            => GpuBufferHelpers.Read<T>(_device, context, Resource, Count, ElementSize);

        /// <summary>
        /// Copies the current GPU contents into an existing CPU array.
        /// </summary>
        public void GetData(ID3D11DeviceContext context, Span<T> destination)
            => GpuBufferHelpers.Read<T>(_device, context, Resource, Count, ElementSize, destination);

        /// <summary>
        /// Uploads new contents into the buffer. A shorter span updates only the
        /// leading elements.
        /// </summary>
        public void SetData(ID3D11DeviceContext context, ReadOnlySpan<T> data)
            => GpuBufferHelpers.Write(context, Resource, data, Count);

        /// <inheritdoc/>
        public void Dispose()
        {
            Srv?.Dispose();
            Uav?.Dispose();
            Resource?.Dispose();
        }
    }
}

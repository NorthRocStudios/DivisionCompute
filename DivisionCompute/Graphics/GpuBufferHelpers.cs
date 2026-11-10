//
// Copyright (c) 2026 Rex Woodfield and DivisionCompute contributors
//
// This file is part of DivisionCompute and is subject to the terms
// of the DivisionCompute License. See the LICENSE.txt file in the
// project root for full license terms.
//
using Vortice.Direct3D11;

namespace DivisionEngine.Graphics
{
    /// <summary>
    /// Shared validation, upload, and readback plumbing for the typed GPU buffer
    /// wrappers.
    /// </summary>
    internal static class GpuBufferHelpers
    {
        /// <summary>
        /// Validates the element layout and returns its stride in bytes.
        /// </summary>
        public static int GetStride<T>() where T : unmanaged
        {
            int stride = ShaderStructLayout.GetStride<T>();
            if (stride <= 0 || stride > 2048 || stride % 4 != 0)
                throw new NotSupportedException(
                    $"Type '{typeof(T).Name}' has a {stride}-byte stride. D3D11 structured-buffer strides must be a positive multiple of 4 and at most 2048 bytes.");
            return stride;
        }

        /// <summary>
        /// Validates that a type has a typed DXGI format and returns its element size.
        /// </summary>
        public static int GetTypedElementSize<T>() where T : unmanaged
        {
            _ = DXGIFormatMapper.GetFormat<T>();
            return System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        }

        /// <summary>
        /// Validates an element count and its total byte size.
        /// </summary>
        public static void ValidateCount(int count, int stride)
        {
            if (count <= 0)
                throw new ArgumentOutOfRangeException(nameof(count), count, "A GPU buffer must contain at least one element.");

            long sizeInBytes = (long)count * stride;
            if (sizeInBytes > uint.MaxValue)
                throw new ArgumentOutOfRangeException(
                    nameof(count), count, $"The buffer would be {sizeInBytes} bytes, which exceeds the D3D11 buffer size limit.");
        }

        /// <summary>
        /// Copies a GPU buffer into a new CPU array.
        /// </summary>
        public static T[] Read<T>(
            ID3D11Device device,
            ID3D11DeviceContext context,
            ID3D11Buffer buffer,
            int count,
            int stride) where T : unmanaged
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(buffer);
            ValidateCount(count, stride);

            uint sizeInBytes = checked((uint)((long)count * stride));
            using ID3D11Buffer staging = device.CreateBuffer(
                sizeInBytes,
                BindFlags.None,
                ResourceUsage.Staging,
                CpuAccessFlags.Read);

            context.CopyResource(staging, buffer);
            MappedSubresource mapped = context.Map(staging, 0, MapMode.Read, MapFlags.None);
            try
            {
                var result = new T[count];
                unsafe
                {
                    fixed (T* destination = result)
                    {
                        System.Buffer.MemoryCopy(
                            mapped.DataPointer.ToPointer(),
                            destination,
                            sizeInBytes,
                            sizeInBytes);
                    }
                }
                return result;
            }
            finally
            {
                context.Unmap(staging, 0);
            }
        }

        /// <summary>
        /// Uploads CPU data into the start of a GPU buffer.
        /// </summary>
        public static void Write<T>(
            ID3D11DeviceContext context,
            ID3D11Buffer buffer,
            ReadOnlySpan<T> data,
            int count) where T : unmanaged
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(buffer);

            if (data.Length > count)
                throw new ArgumentException(
                    $"The source contains {data.Length} elements but the buffer only has {count}.",
                    nameof(data));

            if (data.IsEmpty)
                return;

            context.UpdateSubresource(data, buffer);
        }
    }
}

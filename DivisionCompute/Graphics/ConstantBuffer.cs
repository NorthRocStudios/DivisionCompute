//
// Copyright (c) 2026 Rex Woodfield and DivisionCompute contributors
//
// This file is part of DivisionCompute and is subject to the terms
// of the DivisionCompute License. See the LICENSE.txt file in the project root
// for full license terms.
//
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace DivisionEngine.Graphics
{
    /// <summary>
    /// A GPU constant buffer holding one value of <typeparamref name="T"/>, plus the
    /// shader-side marker for an HLSL <c>cbuffer</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declare as a <c>[ShaderResource]</c> field in a shader struct and read its
    /// values in shader code through <see cref="Value"/>:
    /// </para>
    /// <code>
    /// public struct GradeConstants
    /// {
    ///     public float width;
    ///     public float hueShift;
    /// }
    ///
    /// [Shader]
    /// public struct GradeShader
    /// {
    ///     [ShaderResource] public ConstantBuffer&lt;GradeConstants&gt; constants;
    ///     [ShaderResource] public RWTexture2D&lt;float4&gt; output;
    ///
    ///     [Kernel(8, 8, 1)]
    ///     public void Execute(uint3 id)
    ///     {
    ///         float w = constants.Value.width;
    ///     }
    /// }
    /// </code>
    /// <para>
    /// The translator emits <c>cbuffer constants : register(b0) { float constants_width; ... }</c>
    /// and rewrites <c>constants.Value.width</c> to <c>constants_width</c>. The
    /// <c>&lt;fieldName&gt;_</c> prefix keeps cbuffer members unique across buffers and
    /// impossible to shadow with a shader local.
    /// </para>
    /// <para>
    /// Fields are packed with the Direct3D constant-buffer rules (16-byte registers,
    /// no member may straddle a register boundary) and the CPU-side upload applies the
    /// same layout, so C# does not need to reproduce the HLSL padding by hand. Only
    /// scalars, DivisionMath vectors, and int/uint-backed enums are supported; nested
    /// structs, arrays, matrices, and <c>bool</c> are rejected because their HLSL
    /// constant-buffer layout does not match the C# layout.
    /// </para>
    /// <para>
    /// The buffer is bound through <see cref="DivisionShader.SetConstantBuffer{T}(string, ConstantBuffer{T})"/>.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">An unmanaged struct of scalars, vectors, or int/uint enums.</typeparam>
    public sealed class ConstantBuffer<T> : IDisposable where T : unmanaged
    {
        private readonly ID3D11Device _device;
        private readonly byte[] _scratch;
        private readonly GpuConstantBufferLayout _layout;

        /// <summary>
        /// The underlying D3D11 constant buffer.
        /// </summary>
        public ID3D11Buffer Buffer { get; }

        /// <summary>
        /// Size of the buffer in bytes. Always a multiple of 16, as D3D11 requires
        /// for constant buffers.
        /// </summary>
        public int ByteSize { get; }

        /// <summary>
        /// Shader-only accessor: reads the constant buffer's fields in shader code.
        /// Use <see cref="SetData"/> on the CPU instead.
        /// </summary>
        /// <exception cref="NotSupportedException">Always, when called on the CPU.</exception>
        public T Value => throw new NotSupportedException(
            "ConstantBuffer.Value is only available in shader code. Use SetData on the CPU.");

        /// <summary>
        /// Creates a constant buffer and uploads <paramref name="value"/>.
        /// </summary>
        /// <param name="device">The D3D11 device that owns the buffer.</param>
        /// <param name="value">The initial contents.</param>
        /// <exception cref="NotSupportedException">If <typeparamref name="T"/> has an unsupported field.</exception>
        public ConstantBuffer(ID3D11Device device, in T value)
        {
            ArgumentNullException.ThrowIfNull(device);

            _device = device;
            _layout = GpuConstantBufferLayout.For(typeof(T));
            ByteSize = _layout.PaddedSize;
            _scratch = new byte[ByteSize];

            _layout.Pack(in value, _scratch);

            Buffer = device.CreateBuffer(
                _scratch,
                BindFlags.ConstantBuffer,
                ResourceUsage.Default,
                CpuAccessFlags.None,
                ResourceOptionFlags.None,
                sizeInBytes: checked((uint)ByteSize),
                structureByteStride: 0);
        }

        /// <summary>
        /// Uploads new contents into the constant buffer. Call this before
        /// dispatching a shader that reads the buffer.
        /// </summary>
        /// <param name="context">The immediate context used for the upload.</param>
        /// <param name="value">The new contents.</param>
        public void SetData(ID3D11DeviceContext context, in T value)
        {
            ArgumentNullException.ThrowIfNull(context);
            _layout.Pack(in value, _scratch);
            context.UpdateSubresource<byte>(_scratch, Buffer);
        }

        /// <summary>
        /// Reads the constant buffer back as packed bytes in the HLSL layout.
        /// Intended for tests and diagnostics.
        /// </summary>
        /// <param name="context">The immediate context used for the copy and map.</param>
        public byte[] GetPackedData(ID3D11DeviceContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            using ID3D11Buffer staging = _device.CreateBuffer(
                checked((uint)ByteSize),
                BindFlags.None,
                ResourceUsage.Staging,
                CpuAccessFlags.Read);

            context.CopyResource(staging, Buffer);
            MappedSubresource mapped = context.Map(staging, 0, MapMode.Read, MapFlags.None);
            try
            {
                byte[] result = new byte[ByteSize];
                Marshal.Copy(mapped.DataPointer, result, 0, ByteSize);
                return result;
            }
            finally
            {
                context.Unmap(staging, 0);
            }
        }

        /// <inheritdoc/>
        public void Dispose() => Buffer?.Dispose();
    }
}

namespace DivisionEngine
{
    /// <summary>
    /// One field of a constant buffer after HLSL register packing.
    /// </summary>
    /// <param name="Field">The reflected backing field.</param>
    /// <param name="ManagedOffset">Offset of the field inside the C# struct.</param>
    /// <param name="Size">Size of the field in bytes.</param>
    /// <param name="Offset">Packed offset inside the HLSL constant buffer.</param>
    internal readonly record struct ConstantBufferField(
        FieldInfo Field,
        int ManagedOffset,
        int Size,
        int Offset);

    /// <summary>
    /// Computes and applies the Direct3D constant-buffer layout for a struct.
    /// </summary>
    /// <remarks>
    /// <para>
    /// D3D packs constants into 16-byte registers and refuses to let a member
    /// straddle a register boundary: a member that would cross one starts at the
    /// next register instead. That is the only rule needed for scalars and
    /// vectors, so the layout here is a single left-to-right pass.
    /// </para>
    /// <para>
    /// Values are copied field by field using the reflected managed offsets, so the
    /// C# struct never has to reproduce the HLSL padding itself. That is also why
    /// only types whose managed and unmanaged layouts agree may be used.
    /// </para>
    /// </remarks>
    internal sealed class GpuConstantBufferLayout
    {
        private const int RegisterSize = 16;

        private static readonly Dictionary<Type, GpuConstantBufferLayout> Cache = [];
        private static readonly Lock CacheLock = new();

        private readonly ConstantBufferField[] _fields;

        /// <summary>Total size including the trailing register padding.</summary>
        public int PaddedSize { get; }

        /// <summary>The packed fields in declaration order.</summary>
        public IReadOnlyList<ConstantBufferField> Fields => _fields;

        private GpuConstantBufferLayout(Type type)
        {
            var fields = new List<ConstantBufferField>();
            int offset = 0;

            foreach (FieldInfo field in GetUniformFields(type))
            {
                int size = GetFieldSize(field.FieldType, type.Name, field.Name);
                if (offset % RegisterSize + size > RegisterSize)
                    offset = RoundUpToRegister(offset);

                fields.Add(new ConstantBufferField(field, ManagedOffset(type, field), size, offset));
                offset += size;
            }

            _fields = [.. fields];
            PaddedSize = Math.Max(RoundUpToRegister(offset), RegisterSize);
        }

        /// <summary>
        /// Gets the cached layout for <paramref name="type"/>, validating it first.
        /// </summary>
        public static GpuConstantBufferLayout For(Type type)
        {
            ArgumentNullException.ThrowIfNull(type);

            lock (CacheLock)
            {
                if (Cache.TryGetValue(type, out GpuConstantBufferLayout? cached))
                    return cached;

                GpuConstantBufferLayout layout = new(type);
                Cache[type] = layout;
                return layout;
            }
        }

        /// <summary>
        /// Packs <paramref name="value"/> into <paramref name="destination"/> using
        /// the HLSL layout. <paramref name="destination"/> must be at least
        /// <see cref="PaddedSize"/> bytes. Padding bytes are left untouched.
        /// </summary>
        public void Pack<T>(in T value, Span<byte> destination) where T : unmanaged
        {
            ReadOnlySpan<byte> source = MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in value), 1));

            foreach (ConstantBufferField field in _fields)
            {
                if (field.ManagedOffset + field.Size > source.Length)
                    throw new NotSupportedException(
                        $"Field '{field.Field.Name}' does not fit inside '{typeof(T).Name}'. " +
                        "Use public float, int, uint, or DivisionMath vector fields.");

                source.Slice(field.ManagedOffset, field.Size)
                    .CopyTo(destination.Slice(field.Offset, field.Size));
            }
        }

        /// <summary>
        /// Returns the instance fields of a constant-buffer struct in declaration
        /// order, validating each one.
        /// </summary>
        private static IReadOnlyList<FieldInfo> GetUniformFields(Type type)
        {
            if (!type.IsValueType)
                throw new NotSupportedException(
                    $"Type '{type.Name}' is not a value type. Constant buffers must hold a struct.");

            if (type.IsGenericType && !type.IsGenericTypeDefinition)
                throw new NotSupportedException(
                    $"Generic type '{type.Name}' cannot be used as a constant buffer.");

            if (type.IsExplicitLayout || type.IsAutoLayout || !type.IsLayoutSequential)
                throw new NotSupportedException(
                    $"Struct '{type.Name}' must use sequential layout to be used as a constant buffer.");

            if (type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length > 0)
                throw new NotSupportedException(
                    $"Struct '{type.Name}' has properties. Constant-buffer contents must be public instance fields.");

            FieldInfo[] fields = type
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(field => !field.IsStatic)
                .OrderBy(field => field.MetadataToken)
                .ToArray();

            if (fields.Length == 0)
                throw new NotSupportedException(
                    $"Struct '{type.Name}' has no instance fields and cannot be used as a constant buffer.");

            foreach (FieldInfo field in fields)
            {
                if (field.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
                    throw new NotSupportedException(
                        $"Field '{type.Name}.{field.Name}' is compiler-generated. Constant-buffer contents must declare their storage as ordinary fields.");

                _ = GetFieldSize(field.FieldType, type.Name, field.Name);
            }

            return fields;
        }

        private static int ManagedOffset(Type type, FieldInfo field)
        {
            try
            {
                return (int)Marshal.OffsetOf(type, field.Name);
            }
            catch (Exception ex)
            {
                throw new NotSupportedException(
                    $"Could not determine the layout offset of '{type.Name}.{field.Name}'. " +
                    "Constant-buffer structs must be blittable.", ex);
            }
        }

        private static int GetFieldSize(Type type, string structName, string fieldName)
        {
            Type underlying = type.IsEnum ? Enum.GetUnderlyingType(type) : type;

            if (type.IsEnum && underlying != typeof(int) && underlying != typeof(uint))
                throw new NotSupportedException(
                    $"Enum '{type.Name}' uses '{underlying.Name}'. Constant-buffer enums must be backed by int or uint.");

            if (underlying == typeof(float) || underlying == typeof(int) || underlying == typeof(uint))
                return 4;

            if (underlying == typeof(double))
                throw new NotSupportedException(
                    "double fields are not supported in constant buffers yet. Use float.");

            if (type.IsArray || type.IsPointer || type.IsByRef)
                throw new NotSupportedException(
                    $"Type '{type.Name}' cannot be used in a constant buffer. Arrays, pointers, and by-ref fields are not supported.");

            int? vectorSize = GetVectorSize(type);
            if (vectorSize is not null)
                return vectorSize.Value;

            throw new NotSupportedException(
                $"Field '{structName}.{fieldName}' has type '{type.Name}', which is not supported in a constant buffer. " +
                "Use float, int, uint, an int/uint enum, or a DivisionMath float/int/uint vector.");
        }

        /// <summary>
        /// Returns the byte size of a DivisionMath vector type, or <c>null</c> when
        /// the type is not one.
        /// </summary>
        private static int? GetVectorSize(Type type) => type.Name switch
        {
            "float2" or "int2" or "uint2" => 8,
            "float3" or "int3" or "uint3" => 12,
            "float4" or "int4" or "uint4" => 16,
            _ => null,
        };

        private static int RoundUpToRegister(int value) => (value + RegisterSize - 1) / RegisterSize * RegisterSize;
    }
}

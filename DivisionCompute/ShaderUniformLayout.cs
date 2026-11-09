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

namespace DivisionEngine
{
    /// <summary>
    /// The HLSL type of a shader uniform, used to validate setter calls.
    /// </summary>
    internal enum UniformKind
    {
        /// <summary><c>float</c></summary>
        Float,
        /// <summary><c>int</c></summary>
        Int,
        /// <summary><c>uint</c></summary>
        UInt,
        /// <summary><c>bool</c> (32-bit in a constant buffer)</summary>
        Bool,
        /// <summary><c>float2</c></summary>
        Float2,
        /// <summary><c>float3</c></summary>
        Float3,
        /// <summary><c>float4</c></summary>
        Float4,
        /// <summary><c>int2</c></summary>
        Int2,
        /// <summary><c>int3</c></summary>
        Int3,
        /// <summary><c>int4</c></summary>
        Int4,
        /// <summary><c>uint2</c></summary>
        UInt2,
        /// <summary><c>uint3</c></summary>
        UInt3,
        /// <summary><c>uint4</c></summary>
        UInt4,
    }

    /// <summary>
    /// One shader uniform after HLSL constant-buffer packing.
    /// </summary>
    /// <param name="Name">The C# field name, and the HLSL member name.</param>
    /// <param name="Kind">The HLSL type.</param>
    /// <param name="Size">Size in bytes.</param>
    /// <param name="Offset">Packed offset inside the parameter constant buffer.</param>
    internal readonly record struct UniformField(string Name, UniformKind Kind, int Size, int Offset);

    /// <summary>
    /// Describes the shader uniforms of a <c>[Shader]</c> struct: which plain
    /// fields form the parameter constant buffer, their HLSL types, and the byte
    /// offset each value occupies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every non-<c>[ShaderResource]</c> instance field becomes a uniform that the
    /// translator emits inside <c>cbuffer DivisionParams : register(b0)</c>. The
    /// CPU side packs values into the same layout, so a shader can be driven with
    /// <c>shader.SetFloat("width", 1280)</c> exactly like a Unity compute shader.
    /// </para>
    /// <para>
    /// Layout follows the Direct3D constant-buffer rules: values are packed into
    /// 16-byte registers and no member may straddle a register boundary. Because
    /// values are written per field into the packed buffer, the C# struct layout is
    /// irrelevant - it only has to expose public fields with supported types.
    /// </para>
    /// </remarks>
    internal sealed class ShaderUniformLayout
    {
        private const int RegisterSize = 16;
        private const string ShaderResourceAttributeName = "ShaderResourceAttribute";

        private static readonly Dictionary<Type, ShaderUniformLayout> Cache = [];
        private static readonly Lock CacheLock = new();

        private readonly Dictionary<string, UniformField> _byName;

        /// <summary>
        /// The size of the parameter constant buffer in bytes. Always a positive
        /// multiple of 16, as D3D11 requires.
        /// </summary>
        public int ByteSize { get; }

        /// <summary>
        /// The uniforms in declaration order.
        /// </summary>
        public IReadOnlyList<UniformField> Fields { get; }

        private ShaderUniformLayout(Type shaderType)
        {
            var fields = new List<UniformField>();
            int offset = 0;

            foreach (FieldInfo field in GetUniformSourceFields(shaderType))
            {
                UniformKind kind = GetKind(field.FieldType, shaderType, field);
                int size = GetSize(kind);

                if (offset % RegisterSize + size > RegisterSize)
                    offset = RoundUpToRegister(offset);

                fields.Add(new UniformField(field.Name, kind, size, offset));
                offset += size;
            }

            Fields = fields;
            _byName = fields.ToDictionary(f => f.Name, f => f, StringComparer.Ordinal);
            ByteSize = Math.Max(RoundUpToRegister(offset), RegisterSize);
        }

        /// <summary>
        /// Gets the cached uniform layout for a shader type, validating it first.
        /// Shaders without uniforms produce an empty layout.
        /// </summary>
        public static ShaderUniformLayout For(Type shaderType)
        {
            ArgumentNullException.ThrowIfNull(shaderType);

            lock (CacheLock)
            {
                if (Cache.TryGetValue(shaderType, out ShaderUniformLayout? cached))
                    return cached;

                ShaderUniformLayout layout = new(shaderType);
                Cache[shaderType] = layout;
                return layout;
            }
        }

        /// <summary>
        /// Looks up a uniform by name.
        /// </summary>
        public bool TryGet(string name, out UniformField field)
            => _byName.TryGetValue(name, out field);

        /// <summary>
        /// Returns the uniform field names, in declaration order.
        /// </summary>
        public IEnumerable<string> Names => Fields.Select(f => f.Name);

        /// <summary>
        /// Returns the plain instance fields of the shader struct in declaration
        /// order, skipping <c>[ShaderResource]</c> and static fields.
        /// </summary>
        private static IReadOnlyList<FieldInfo> GetUniformSourceFields(Type shaderType)
        {
            if (!shaderType.IsValueType)
                throw new NotSupportedException(
                    $"Shader type '{shaderType.Name}' must be a struct.");

            return [.. shaderType
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(field => !field.IsStatic)
                .Where(field => !HasShaderResourceAttribute(field))
                .OrderBy(field => field.MetadataToken)];
        }

        private static bool HasShaderResourceAttribute(FieldInfo field)
            => field.GetCustomAttributes(inherit: false)
                .Any(attribute => attribute.GetType().Name == ShaderResourceAttributeName);

        private static UniformKind GetKind(Type type, Type shaderType, FieldInfo field)
        {
            if (type.IsEnum)
            {
                Type underlying = Enum.GetUnderlyingType(type);
                if (underlying == typeof(int)) return UniformKind.Int;
                if (underlying == typeof(uint)) return UniformKind.UInt;

                throw new NotSupportedException(
                    $"Uniform '{shaderType.Name}.{field.Name}' uses enum '{type.Name}' backed by '{underlying.Name}'. " +
                    "Shader uniforms must use int or uint enums.");
            }

            if (type == typeof(float)) return UniformKind.Float;
            if (type == typeof(int)) return UniformKind.Int;
            if (type == typeof(uint)) return UniformKind.UInt;
            if (type == typeof(bool)) return UniformKind.Bool;

            if (type == typeof(double))
                throw new NotSupportedException(
                    $"Uniform '{shaderType.Name}.{field.Name}' is a double. Use float; doubles are not supported in shader uniforms yet.");

            string name = type.Name;
            return name switch
            {
                "float2" => UniformKind.Float2,
                "float3" => UniformKind.Float3,
                "float4" => UniformKind.Float4,
                "int2" => UniformKind.Int2,
                "int3" => UniformKind.Int3,
                "int4" => UniformKind.Int4,
                "uint2" => UniformKind.UInt2,
                "uint3" => UniformKind.UInt3,
                "uint4" => UniformKind.UInt4,
                _ => throw new NotSupportedException(
                    $"Uniform '{shaderType.Name}.{field.Name}' has type '{type.Name}', which is not supported. " +
                    "Shader uniforms must be float, int, uint, bool, an int/uint enum, or a DivisionMath " +
                    "float/int/uint vector. Move other data into a structured buffer or a texture."),
            };
        }

        private static int GetSize(UniformKind kind) => kind switch
        {
            UniformKind.Float or UniformKind.Int or UniformKind.UInt or UniformKind.Bool => 4,
            UniformKind.Float2 or UniformKind.Int2 or UniformKind.UInt2 => 8,
            UniformKind.Float3 or UniformKind.Int3 or UniformKind.UInt3 => 12,
            UniformKind.Float4 or UniformKind.Int4 or UniformKind.UInt4 => 16,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown uniform kind."),
        };

        /// <summary>
        /// The HLSL type name of a uniform.
        /// </summary>
        public static string GetHlslTypeName(UniformKind kind) => kind switch
        {
            UniformKind.Float => "float",
            UniformKind.Int => "int",
            UniformKind.UInt => "uint",
            UniformKind.Bool => "bool",
            UniformKind.Float2 => "float2",
            UniformKind.Float3 => "float3",
            UniformKind.Float4 => "float4",
            UniformKind.Int2 => "int2",
            UniformKind.Int3 => "int3",
            UniformKind.Int4 => "int4",
            UniformKind.UInt2 => "uint2",
            UniformKind.UInt3 => "uint3",
            UniformKind.UInt4 => "uint4",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown uniform kind."),
        };

        private static int RoundUpToRegister(int value) => (value + RegisterSize - 1) / RegisterSize * RegisterSize;

        /// <summary>
        /// Writes one value into the packed uniform buffer.
        /// </summary>
        public static void Write<T>(Span<byte> destination, in UniformField field, in T value)
            where T : unmanaged
        {
            Span<byte> target = destination.Slice(field.Offset, field.Size);

            // bool is 4 bytes in an HLSL constant buffer but 1 byte in C#; write
            // the 0/1 flag explicitly instead of copying the managed representation.
            if (field.Kind == UniformKind.Bool && typeof(T) == typeof(bool))
            {
                bool flag = Unsafe.As<T, bool>(ref Unsafe.AsRef(in value));
                MemoryMarshal.Write(target, flag ? 1u : 0u);
                return;
            }

            if (Unsafe.SizeOf<T>() != field.Size)
                throw new ArgumentException(
                    $"Uniform '{field.Name}' is {GetHlslTypeName(field.Kind)} ({field.Size} bytes) " +
                    $"but the value is {Unsafe.SizeOf<T>()} bytes.");

            MemoryMarshal.Write(target, in value);
        }
    }
}

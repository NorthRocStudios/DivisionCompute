//
// Copyright (c) 2026 Rex Woodfield and DivisionCompute contributors
//
// This file is part of DivisionCompute and is subject to the terms
// of the DivisionCompute License. See the LICENSE.txt file in the
// project root for full license terms.
//
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace DivisionEngine
{
    /// <summary>
    /// Validates and describes the C# structs that can be placed in a structured
    /// buffer, and emits the matching HLSL struct declarations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A structured-buffer element must have an unmanaged, sequential layout that
    /// both C# and HLSL agree on. This type deliberately supports a conservative
    /// subset - scalars, DivisionMath vectors, enums with an int/uint backing type,
    /// and nested structs made from those - and rejects layouts that would otherwise
    /// produce silently corrupted GPU data.
    /// </para>
    /// <para>
    /// Matrices, <see cref="bool"/>, <see cref="char"/>, reference types, arrays,
    /// properties, and explicit/packed layouts are rejected for now. They can be
    /// added later with an explicit layout contract.
    /// </para>
    /// </remarks>
    internal static class ShaderStructLayout
    {
        private const int MaxStructuredBufferStride = 2048;

        private static readonly Dictionary<Type, string> ScalarTypeNames = new()
        {
            [typeof(float)] = "float",
            [typeof(double)] = "double",
            [typeof(int)] = "int",
            [typeof(uint)] = "uint",
            [typeof(bool)] = "bool",
            [typeof(Half)] = "half",
        };

        private static readonly Dictionary<Type, string> SystemNumericsVectorNames = new()
        {
            [typeof(System.Numerics.Vector2)] = "float2",
            [typeof(System.Numerics.Vector3)] = "float3",
            [typeof(System.Numerics.Vector4)] = "float4",
        };

        private static readonly HashSet<string> VectorTypeNames = BuildVectorTypeNames();
        private static readonly HashSet<string> MatrixTypeNames = BuildMatrixTypeNames();

        private static readonly HashSet<string> HlslKeywords = new(StringComparer.Ordinal)
        {
            "asm", "bool", "break", "case", "cbuffer", "centroid", "class", "column_major",
            "compile", "const", "continue", "default", "discard", "do", "double", "else",
            "extern", "false", "float", "for", "half", "if", "in", "inline", "inout", "int",
            "matrix", "namespace", "nointerpolation", "noperspective", "out", "packoffset",
            "pass", "pixelshader", "precise", "return", "register", "row_major", "sample",
            "sampler", "sampler1D", "sampler2D", "sampler3D", "samplerCUBE", "sampler_state",
            "shared", "static", "string", "struct", "switch", "technique", "technique10",
            "technique11", "texture", "texture1D", "texture2D", "texture3D", "textureCube",
            "true", "typedef", "uint", "uniform", "vector", "vertexshader", "void", "volatile",
            "while",
        };

        private static readonly MethodInfo UnsafeSizeOfMethod = typeof(Unsafe).GetMethod(
            nameof(Unsafe.SizeOf), BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate Unsafe.SizeOf<T>().");

        /// <summary>
        /// True when the type is represented directly by an HLSL scalar, vector,
        /// matrix, or well-known System.Numerics vector.
        /// </summary>
        public static bool IsBuiltIn(Type type) => GetBuiltInTypeName(type) is not null;

        /// <summary>
        /// True when the type is one of the supported HLSL vector names.
        /// </summary>
        public static bool IsVector(Type type) => VectorTypeNames.Contains(type.Name);

        /// <summary>
        /// True when the type is one of the supported HLSL matrix names.
        /// </summary>
        public static bool IsMatrix(Type type) => MatrixTypeNames.Contains(type.Name);

        /// <summary>
        /// Gets the HLSL type name for a scalar, vector, enum, or struct.
        /// </summary>
        public static string GetHlslTypeName(Type type)
        {
            ArgumentNullException.ThrowIfNull(type);

            string? builtInName = GetBuiltInTypeName(type);
            if (builtInName is not null) return builtInName;

            if (type.IsEnum)
            {
                Type underlying = Enum.GetUnderlyingType(type);
                if (underlying != typeof(int) && underlying != typeof(uint))
                    throw new NotSupportedException(
                        $"Enum '{type.Name}' uses '{underlying.Name}'. Structured-buffer enums must use int or uint.");
                return GetHlslTypeName(underlying);
            }

            if (type.IsGenericType)
                throw new NotSupportedException(
                    $"Generic type '{type.Name}' cannot be used in a structured buffer. Use a non-generic sequential struct.");

            if (!type.IsValueType)
                throw new NotSupportedException(
                    $"Reference type '{type.Name}' cannot be used in a structured buffer.");

            string name = type.Name;
            ValidateHlslIdentifier(name, $"struct '{name}'");
            return name;
        }

        /// <summary>
        /// Gets the element stride in bytes and validates the entire element layout.
        /// </summary>
        public static int GetStride<T>() where T : unmanaged
        {
            ValidateElementType(typeof(T));
            return Unsafe.SizeOf<T>();
        }

        /// <summary>
        /// Gets the element stride in bytes for a runtime type.
        /// </summary>
        public static int GetStride(Type type)
        {
            ValidateElementType(type);
            return GetUnsafeSize(type);
        }

        /// <summary>
        /// Returns the instance fields in declaration order.
        /// </summary>
        public static IReadOnlyList<FieldInfo> GetSerializableFields(Type type)
        {
            FieldInfo[] fields = type
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(field => !field.IsStatic)
                .OrderBy(field => field.MetadataToken)
                .ToArray();

            if (fields.Length == 0)
                throw new NotSupportedException(
                    $"Struct '{type.Name}' has no instance fields and cannot be used as a structured-buffer element.");

            foreach (FieldInfo field in fields)
            {
                if (field.IsDefined(typeof(FixedBufferAttribute), inherit: false))
                    throw new NotSupportedException(
                        $"Field '{type.Name}.{field.Name}' is a fixed buffer. Use a fixed-size struct or a separate buffer instead.");

                if (field.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false))
                    throw new NotSupportedException(
                        $"Field '{type.Name}.{field.Name}' is compiler-generated. Structured-buffer elements must declare their storage as ordinary fields.");

                ValidateHlslIdentifier(field.Name, $"field '{type.Name}.{field.Name}'");
            }

            return fields;
        }

        /// <summary>
        /// Builds an HLSL struct declaration for <paramref name="type"/>.
        /// Nested struct declarations must be emitted before this one by the caller.
        /// </summary>
        public static string BuildStructDeclaration(Type type)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"struct {GetHlslTypeName(type)}");
            builder.AppendLine("{");

            foreach (FieldInfo field in GetSerializableFields(type))
                builder.AppendLine($"    {GetHlslTypeName(field.FieldType)} {field.Name};");

            builder.AppendLine("};");
            return builder.ToString();
        }

        /// <summary>
        /// Validates that a type can be used as a structured-buffer element.
        /// </summary>
        public static void ValidateElementType(Type type)
        {
            ArgumentNullException.ThrowIfNull(type);
            ValidateElementType(type, new HashSet<Type>());
        }

        private static void ValidateElementType(Type type, HashSet<Type> visiting)
        {
            if (type == typeof(void) || type.IsByRef || type.IsByRefLike || type.IsPointer || type.IsArray)
                throw new NotSupportedException(
                    $"Type '{type.Name}' cannot be used in a structured buffer. Arrays, pointers, by-ref types, and ref structs are not supported.");

            if (!type.IsValueType || type == typeof(string))
                throw new NotSupportedException(
                    $"Type '{type.Name}' is not a value type. Structured-buffer elements must be unmanaged structs.");

            if (type.IsGenericType)
                throw new NotSupportedException(
                    $"Generic type '{type.Name}' cannot be used in a structured buffer. Use a non-generic sequential struct.");

            if (type.IsEnum)
            {
                Type underlying = Enum.GetUnderlyingType(type);
                if (underlying != typeof(int) && underlying != typeof(uint))
                    throw new NotSupportedException(
                        $"Enum '{type.Name}' uses '{underlying.Name}'. Structured-buffer enums must use int or uint.");
                return;
            }

            if (type == typeof(bool))
                throw new NotSupportedException(
                    "bool fields are not supported in structured buffers because C# bool is 1 byte and HLSL bool is 4 bytes. Use int or uint instead.");

            if (type == typeof(char))
                throw new NotSupportedException(
                    "char fields are not supported in structured buffers. Use int or uint instead.");

            if (type == typeof(Half))
                throw new NotSupportedException(
                    "Half fields are not supported in structured buffers yet. Use float instead.");

            if (type.IsPrimitive)
            {
                if (type == typeof(float) || type == typeof(double) || type == typeof(int) || type == typeof(uint))
                    return;

                throw new NotSupportedException(
                    $"Primitive type '{type.Name}' is not supported in structured buffers. Use float, double, int, or uint.");
            }

            if (IsVector(type)) return;

            if (IsMatrix(type))
                throw new NotSupportedException(
                    $"Matrix type '{type.Name}' is not supported in structured buffers yet. Use float4 fields and matrix math in the shader instead.");

            if (type == typeof(decimal) || type == typeof(IntPtr) || type == typeof(UIntPtr))
                throw new NotSupportedException(
                    $"Type '{type.Name}' cannot be used in a structured buffer.");

            if (type.IsExplicitLayout || type.IsAutoLayout || !type.IsLayoutSequential)
                throw new NotSupportedException(
                    $"Struct '{type.Name}' must use sequential layout. Explicit, auto, and packed layouts are not supported.");

            StructLayoutAttribute? layout = type.StructLayoutAttribute;
            if (layout is not null)
            {
                if (layout.Value == LayoutKind.Explicit)
                    throw new NotSupportedException(
                        $"Struct '{type.Name}' uses explicit layout, which is not supported in structured buffers.");

                if (layout.Pack != 0 && layout.Pack != 8)
                    throw new NotSupportedException(
                        $"Struct '{type.Name}' uses Pack={layout.Pack}. Only the default sequential packing is supported in structured buffers.");

                if (layout.Size != 0)
                    throw new NotSupportedException(
                        $"Struct '{type.Name}' uses an explicit Size. Remove the Size override from its StructLayout attribute.");
            }

            if (type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length > 0)
                throw new NotSupportedException(
                    $"Struct '{type.Name}' has properties. Structured-buffer elements must expose public instance fields so HLSL can address them.");

            if (!visiting.Add(type))
                throw new NotSupportedException(
                    $"Struct '{type.Name}' contains a recursive field cycle and cannot be used in a structured buffer.");

            IReadOnlyList<FieldInfo> fields = GetSerializableFields(type);
            foreach (FieldInfo field in fields)
                ValidateElementType(field.FieldType, visiting);

            int managedSize = GetUnsafeSize(type);
            int marshalSize;
            try
            {
                marshalSize = Marshal.SizeOf(type);
            }
            catch (Exception ex)
            {
                throw new NotSupportedException(
                    $"Struct '{type.Name}' does not have a blittable unmanaged layout: {ex.Message}", ex);
            }

            if (managedSize != marshalSize)
                throw new NotSupportedException(
                    $"Struct '{type.Name}' has conflicting managed and unmanaged sizes ({managedSize} vs {marshalSize} bytes). " +
                    "Use public fields of float, double, int, uint, or DivisionMath vector types.");

            if (managedSize <= 0 || managedSize % 4 != 0)
                throw new NotSupportedException(
                    $"Struct '{type.Name}' has a {managedSize}-byte size. Structured-buffer strides must be a positive multiple of 4 bytes.");

            if (managedSize > MaxStructuredBufferStride)
                throw new NotSupportedException(
                    $"Struct '{type.Name}' is {managedSize} bytes. D3D11 structured-buffer strides cannot exceed {MaxStructuredBufferStride} bytes.");

            visiting.Remove(type);
        }

        private static string? GetBuiltInTypeName(Type type)
        {
            if (ScalarTypeNames.TryGetValue(type, out string? scalarName)) return scalarName;
            if (SystemNumericsVectorNames.TryGetValue(type, out string? systemVectorName)) return systemVectorName;
            if (VectorTypeNames.Contains(type.Name)) return type.Name;
            if (MatrixTypeNames.Contains(type.Name)) return type.Name;
            return null;
        }

        private static int GetUnsafeSize(Type type)
        {
            try
            {
                return (int)UnsafeSizeOfMethod.MakeGenericMethod(type).Invoke(null, null)!;
            }
            catch (Exception ex)
            {
                throw new NotSupportedException(
                    $"Could not determine the unmanaged size of '{type.Name}'.", ex);
            }
        }

        /// <summary>
        /// Validates a shader field name as an HLSL identifier: ASCII letters,
        /// digits, and underscores, and not a reserved word.
        /// </summary>
        /// <param name="name">The field or struct name.</param>
        /// <param name="description">What the name belongs to, for the diagnostic.</param>
        /// <exception cref="NotSupportedException">If the name cannot be emitted into HLSL.</exception>
        internal static void ValidateHlslIdentifier(string name, string description)
        {
            if (string.IsNullOrEmpty(name) ||
                !(char.IsAsciiLetter(name[0]) || name[0] == '_') ||
                name.Skip(1).Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            {
                throw new NotSupportedException(
                    $"The {description} name '{name}' is not a valid HLSL identifier. Use ASCII letters, digits, and underscores.");
            }

            if (HlslKeywords.Contains(name))
                throw new NotSupportedException(
                    $"The {description} name '{name}' is reserved in HLSL. Rename it (for example '{name}Index').");
        }

        private static HashSet<string> BuildVectorTypeNames()
        {
            string[] prefixes = ["float", "int", "uint", "double", "bool", "half"];
            string[] sizes = ["2", "3", "4"];
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (string prefix in prefixes)
                foreach (string size in sizes)
                    names.Add($"{prefix}{size}");

            return names;
        }

        private static HashSet<string> BuildMatrixTypeNames()
        {
            string[] prefixes = ["float", "int", "uint", "double"];
            string[] sizes = ["2", "3", "4"];
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (string prefix in prefixes)
                foreach (string rows in sizes)
                    foreach (string columns in sizes)
                        names.Add($"{prefix}{rows}x{columns}");

            return names;
        }
    }
}

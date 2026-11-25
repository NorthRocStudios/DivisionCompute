//
// Copyright (c) 2026 Rex Woodfield and DivisionCompute contributors
//
// This file is part of DivisionCompute and is subject to the terms
// of the DivisionCompute License. See the LICENSE.txt file in the
// project root for full license terms.
//
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DivisionEngine
{
    /// <summary>
    /// One uniform field discovered from a shader struct's Roslyn symbol.
    /// </summary>
    /// <param name="Name">The C# field name, and the HLSL member name.</param>
    /// <param name="Kind">The HLSL type.</param>
    /// <param name="Size">Size in bytes.</param>
    /// <param name="Offset">Packed offset inside the parameter constant buffer.</param>
    internal readonly record struct SymbolUniformField(string Name, UniformKind Kind, int Size, int Offset);

    /// <summary>
    /// One resource field discovered from a shader struct's Roslyn symbol.
    /// </summary>
    /// <param name="Name">The C# field name.</param>
    /// <param name="Slot">The HLSL register slot.</param>
    /// <param name="Kind">Whether the resource is read (SRV) or written (UAV).</param>
    internal readonly record struct SymbolResourceField(string Name, uint Slot, DivisionResourceKind Kind);

    /// <summary>
    /// Computes shader resource and uniform layouts from Roslyn symbols.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the symbol-based twin of <see cref="ShaderUniformLayout"/> and
    /// <see cref="DivisionComputeContext.GetResourceLayout"/>. The source generator
    /// cannot reflect over the runtime type (it runs inside the C# compiler), and the
    /// runtime translator needs the same answer when it is handed source instead of a
    /// loaded type. Sharing one implementation keeps the generated HLSL, the baked
    /// descriptor, and the reflection path in agreement.
    /// </para>
    /// <para>
    /// This file is compiled into both DivisionCompute and the source generator.
    /// </para>
    /// </remarks>
    internal static class ShaderSymbolLayout
    {
        private const int RegisterSize = 16;
        private const string ShaderResourceAttributeName = "ShaderResourceAttribute";
        private const string UniformBufferName = DivisionShaderConstants.UniformBufferName;

        /// <summary>
        /// Returns the packed uniform fields of a shader struct, in declaration order.
        /// </summary>
        /// <param name="shaderSymbol">The shader struct symbol.</param>
        /// <exception cref="NotSupportedException">If a field cannot be a shader uniform.</exception>
        internal static List<SymbolUniformField> GetUniforms(INamedTypeSymbol shaderSymbol)
        {
            var fields = new List<SymbolUniformField>();
            int offset = 0;

            foreach (IFieldSymbol field in GetUniformSourceFields(shaderSymbol))
            {
                ShaderStructLayout.ValidateHlslIdentifier(field.Name, $"shader uniform '{shaderSymbol.Name}.{field.Name}'");

                UniformKind kind = GetUniformKind(field, shaderSymbol);
                int size = GetUniformSize(kind);

                if (offset % RegisterSize + size > RegisterSize)
                    offset = RoundUpToRegister(offset);

                fields.Add(new SymbolUniformField(field.Name, kind, size, offset));
                offset += size;
            }

            return fields;
        }

        /// <summary>
        /// Returns the byte size of the packed uniform buffer for a set of fields.
        /// </summary>
        /// <param name="fields">The packed fields.</param>
        internal static int GetUniformByteSize(List<SymbolUniformField> fields)
        {
            int end = 0;
            foreach (SymbolUniformField field in fields)
                end = Math.Max(end, field.Offset + field.Size);

            return Math.Max(RoundUpToRegister(end), RegisterSize);
        }

        /// <summary>
        /// Returns the resource bindings of a shader struct, in declaration order.
        /// </summary>
        /// <param name="shaderSymbol">The shader struct symbol.</param>
        /// <exception cref="NotSupportedException">If a resource field is not supported.</exception>
        internal static List<SymbolResourceField> GetResources(INamedTypeSymbol shaderSymbol)
        {
            var resources = new List<SymbolResourceField>();
            uint srvSlot = 0;
            uint uavSlot = 0;

            foreach (IFieldSymbol field in GetInstanceFields(shaderSymbol))
            {
                if (!HasAttribute(field, ShaderResourceAttributeName))
                    continue;

                // The translator injects a default sampler for Texture2D.Sample; an
                // explicit Sampler field is emitted but not bound to a register.
                if (field.Type is not INamedTypeSymbol { IsGenericType: true } fieldType)
                {
                    if (field.Type.Name == "Sampler")
                        continue;

                    throw new NotSupportedException(
                        $"Shader resource field '{shaderSymbol.Name}.{field.Name}' is '{field.Type.Name}'. " +
                        "Only 'Sampler' is allowed as a non-generic resource type.");
                }

                string definitionName = fieldType.Name;
                bool isUav = definitionName is "RWTexture2D" or "RWTexture3D" or "RWBuffer" or "RWStructuredBuffer";
                bool isSrv = definitionName is "Texture2D" or "Texture3D" or "Buffer" or "StructuredBuffer";

                if (!isUav && !isSrv)
                    throw new NotSupportedException(
                        $"Shader resource field '{shaderSymbol.Name}.{field.Name}' has unsupported type '{definitionName}<T>'. " +
                        "Use Buffer<T>, RWBuffer<T>, StructuredBuffer<T>, RWStructuredBuffer<T>, Texture2D<T>, " +
                        "RWTexture2D<T>, Texture3D<T>, or RWTexture3D<T>.");

                resources.Add(new SymbolResourceField(
                    field.Name,
                    isUav ? uavSlot++ : srvSlot++,
                    isUav ? DivisionResourceKind.Uav : DivisionResourceKind.Srv));
            }

            return resources;
        }

        /// <summary>
        /// Returns the plain instance fields of a shader struct in declaration order,
        /// skipping <c>[ShaderResource]</c> and static fields.
        /// </summary>
        internal static List<IFieldSymbol> GetUniformSourceFields(INamedTypeSymbol shaderSymbol)
        {
            var fields = new List<IFieldSymbol>();

            foreach (IFieldSymbol field in GetInstanceFields(shaderSymbol))
            {
                if (HasAttribute(field, ShaderResourceAttributeName))
                    continue;

                fields.Add(field);
            }

            return fields;
        }

        /// <summary>
        /// Maps a uniform field to its HLSL constant-buffer kind.
        /// </summary>
        internal static UniformKind GetUniformKind(IFieldSymbol field, INamedTypeSymbol shaderSymbol)
        {
            ITypeSymbol type = field.Type;

            if (type.TypeKind == TypeKind.Enum)
            {
                SpecialType underlying = ((INamedTypeSymbol)type).EnumUnderlyingType?.SpecialType ?? SpecialType.None;
                if (underlying == SpecialType.System_Int32) return UniformKind.Int;
                if (underlying == SpecialType.System_UInt32) return UniformKind.UInt;

                throw new NotSupportedException(
                    $"Uniform '{shaderSymbol.Name}.{field.Name}' uses an enum that is not backed by int or uint. " +
                    "Shader uniforms must use int or uint enums.");
            }

            switch (type.SpecialType)
            {
                case SpecialType.System_Single: return UniformKind.Float;
                case SpecialType.System_Int32: return UniformKind.Int;
                case SpecialType.System_UInt32: return UniformKind.UInt;
                case SpecialType.System_Boolean: return UniformKind.Bool;
            }

            return type.Name switch
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
                    $"Uniform '{shaderSymbol.Name}.{field.Name}' has type '{type.Name}', which is not supported. " +
                    "Shader uniforms must be float, int, uint, bool, an int/uint enum, or a DivisionMath " +
                    "float/int/uint vector. Move other data into a structured buffer or a texture."),
            };
        }

        /// <summary>
        /// The size in bytes of a uniform kind inside a constant buffer.
        /// </summary>
        internal static int GetUniformSize(UniformKind kind) => kind switch
        {
            UniformKind.Float or UniformKind.Int or UniformKind.UInt or UniformKind.Bool => 4,
            UniformKind.Float2 or UniformKind.Int2 or UniformKind.UInt2 => 8,
            UniformKind.Float3 or UniformKind.Int3 or UniformKind.UInt3 => 12,
            UniformKind.Float4 or UniformKind.Int4 or UniformKind.UInt4 => 16,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown uniform kind."),
        };

        private static List<IFieldSymbol> GetInstanceFields(INamedTypeSymbol shaderSymbol)
        {
            if (!shaderSymbol.IsValueType)
                throw new NotSupportedException(
                    $"Shader type '{shaderSymbol.Name}' must be a struct.");

            var fields = new List<IFieldSymbol>();
            foreach (ISymbol member in shaderSymbol.GetMembers())
            {
                if (member is IFieldSymbol { IsStatic: false } field)
                    fields.Add(field);
            }

            return fields;
        }

        private static bool HasAttribute(IFieldSymbol field, string attributeName)
        {
            foreach (AttributeData attribute in field.GetAttributes())
            {
                if (attribute.AttributeClass?.Name == attributeName)
                    return true;
            }

            return false;
        }

        private static int RoundUpToRegister(int value) => (value + RegisterSize - 1) / RegisterSize * RegisterSize;
    }
}

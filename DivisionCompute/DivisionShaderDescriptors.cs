//
// Copyright (c) 2026 Rex Woodfield and DivisionCompute contributors
//
// This file is part of DivisionCompute and is subject to the terms
// of the DivisionCompute License. See the LICENSE.txt file in the
// project root for full license terms.
//
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace DivisionEngine
{
    /// <summary>
    /// Registry of shader descriptors baked into assemblies by the DivisionCompute
    /// source generator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The generator emits a module initializer per <c>[Shader]</c> struct that calls
    /// <see cref="Register"/>. Because module initializers run when the assembly is
    /// loaded, every shader is available before user code runs, without any Roslyn
    /// work at runtime.
    /// </para>
    /// <para>
    /// This registry is an optimization, never a requirement: when a type has no
    /// descriptor, <see cref="DivisionShaderCompiler.TranslateShader"/> falls back to
    /// the runtime translator. That keeps runtime shader generation and hot reload
    /// working exactly as before.
    /// </para>
    /// </remarks>
    public static class DivisionShaderDescriptors
    {
        private static readonly ConcurrentDictionary<Type, DivisionShaderDescriptor> Registry = new();

        /// <summary>
        /// The number of registered shaders.
        /// </summary>
        public static int Count => Registry.Count;

        /// <summary>
        /// The shader types that have a generated descriptor.
        /// </summary>
        public static IEnumerable<Type> RegisteredTypes => Registry.Keys;

        /// <summary>
        /// Registers the descriptor for a shader type. Called by generated code.
        /// </summary>
        /// <param name="shaderType">The <c>[Shader]</c> struct type.</param>
        /// <param name="descriptor">The descriptor produced at build time.</param>
        /// <exception cref="ArgumentNullException">If either argument is null.</exception>
        public static void Register(Type shaderType, DivisionShaderDescriptor descriptor)
        {
            ArgumentNullException.ThrowIfNull(shaderType);
            ArgumentNullException.ThrowIfNull(descriptor);

            if (shaderType.IsGenericTypeDefinition)
                throw new ArgumentException(
                    $"Shader type '{shaderType}' is a generic type definition; register a closed type.",
                    nameof(shaderType));

            // A generated registration is authoritative: overwriting keeps a repeated
            // module initializer (or a test that re-registers) harmless.
            Registry[shaderType] = descriptor;
        }

        /// <summary>
        /// Removes the descriptor for a shader type, if one is registered.
        /// </summary>
        /// <param name="shaderType">The shader type to unregister.</param>
        /// <returns>True when a descriptor was removed.</returns>
        public static bool Unregister(Type shaderType)
        {
            ArgumentNullException.ThrowIfNull(shaderType);
            return Registry.TryRemove(shaderType, out _);
        }

        /// <summary>
        /// Looks up the generated descriptor for a shader type.
        /// </summary>
        /// <param name="shaderType">The <c>[Shader]</c> struct type.</param>
        /// <param name="descriptor">The descriptor, when found.</param>
        /// <returns>True when the type was generated at build time.</returns>
        public static bool TryGet(Type shaderType, [NotNullWhen(true)] out DivisionShaderDescriptor? descriptor)
        {
            ArgumentNullException.ThrowIfNull(shaderType);
            return Registry.TryGetValue(shaderType, out descriptor);
        }

        /// <summary>
        /// Removes every registration. Intended for tests.
        /// </summary>
        public static void Clear() => Registry.Clear();
    }
}

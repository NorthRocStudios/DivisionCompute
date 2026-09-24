//
// Copyright (c) 2026 Rex Woodfield and DivisionCompute contributors
//
// This file is part of DivisionCompute and is subject to the terms
// of the DivisionCompute License. See the LICENSE.txt file in the
// project root for full license terms.
//
using System.Runtime.CompilerServices;

namespace DivisionEngine
{
    /// <summary>
    /// Marks a struct as a compute shader that DivisionTranslate can translate
    /// to HLSL and run on the GPU.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When applied without arguments (<c>[Shader]</c>), the compiler injects the
    /// source file path via <see cref="CallerFilePathAttribute"/>. That path is
    /// absolute at build time, and DivisionTranslate falls back to searching for
    /// the shader by filename if the absolute path is stale (for example, after
    /// the project has been moved to another machine).
    /// </para>
    /// <para>
    /// You may also pass an explicit path - either absolute or relative - such as
    /// <c>[Shader("Shaders/MyShader.cs")]</c>. Relative paths are resolved
    /// against the app base directory, the containing assembly's directory, the
    /// nearest project root, and the current working directory, in that order.
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Struct)]
    public class ShaderAttribute([CallerFilePath] string sourceFilePath = "") : Attribute
    {
        /// <summary>
        /// Absolute or relative path to the C# file containing the shader struct.
        /// </summary>
        /// <remarks>
        /// Populated automatically from <see cref="CallerFilePathAttribute"/> when
        /// no argument is passed to the attribute. Set explicitly to override the
        /// auto-detected path.
        /// </remarks>
        public string SourceFilePath { get; set; } = sourceFilePath;
    }

    /// <summary>
    /// Marks a method inside a <see cref="ShaderAttribute"/>-decorated struct as a
    /// dispatchable GPU kernel. The method's body is translated to HLSL and compiled
    /// as a compute shader entry point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The decorated method must return <c>void</c> and take a single
    /// <c>uint3 id</c> parameter (the dispatch thread ID). It will be emitted as
    /// <c>void MethodName(uint3 id : SV_DispatchThreadID)</c> in the generated HLSL.
    /// </para>
    /// <para>
    /// The thread-group size specified by this attribute is emitted as the
    /// <c>[numthreads(x, y, z)]</c> attribute above the HLSL entry point and is
    /// read back at dispatch time so <see cref="DivisionShader.Dispatch"/> can
    /// compute total thread counts.
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method)]
    public class KernelAttribute : Attribute
    {
        /// <summary>
        /// Number of threads along X per thread group. Defaults to 64.
        /// </summary>
        public int ThreadsX { get; set; } = 64;

        /// <summary>
        /// Number of threads along Y per thread group. Defaults to 1.
        /// </summary>
        public int ThreadsY { get; set; } = 1;

        /// <summary>
        /// Number of threads along Z per thread group. Defaults to 1.
        /// </summary>
        public int ThreadsZ { get; set; } = 1;

        /// <summary>
        /// Creates a kernel attribute with a default thread-group size of 64 × 1 × 1.
        /// </summary>
        public KernelAttribute()
        {
            ThreadsX = 64;
            ThreadsY = 1;
            ThreadsZ = 1;
        }

        /// <summary>
        /// Creates a kernel attribute with an explicit thread-group size.
        /// </summary>
        /// <param name="threadsX">Number of threads along X per thread group.</param>
        /// <param name="threadsY">Number of threads along Y per thread group.</param>
        /// <param name="threadsZ">Number of threads along Z per thread group.</param>
        public KernelAttribute(int threadsX, int threadsY, int threadsZ)
        {
            ThreadsX = threadsX;
            ThreadsY = threadsY;
            ThreadsZ = threadsZ;
        }
    }

    /// <summary>
    /// Marks a field inside a <see cref="ShaderAttribute"/>-decorated struct as a
    /// GPU resource (texture, buffer, sampler, or constant buffer). The field's
    /// declared type determines which HLSL resource type it translates to, and
    /// the fields are assigned register slots in declaration order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order of <c>[ShaderResource]</c> fields in the struct determines the
    /// HLSL register slots. UAVs are bound to <c>u0</c>, <c>u1</c>, … ; SRVs to
    /// <c>t0</c>, <c>t1</c>, … ; and constant buffers to <c>b0</c>, <c>b1</c>, …
    /// This layout is mirrored on the CPU side by
    /// <see cref="DivisionComputeContext.GetResourceLayout(Type)"/>, so reordering
    /// fields shifts their slots - keep SRV and UAV counts stable when possible
    /// to avoid rebinding surprises.
    /// </para>
    /// <para>
    /// The <see cref="Slot"/> and <see cref="Type"/> properties are reserved for
    /// future explicit-slot support. Currently the runtime derives both from the
    /// field's position and declared type.
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Field)]
    public class ShaderResourceAttribute : Attribute
    {
        /// <summary>
        /// Reserved. When set, will override the auto-assigned register slot.
        /// Currently ignored by the translator, which always uses declaration order.
        /// </summary>
        public int Slot { get; set; }

        /// <summary>
        /// Reserved. When set, will hint at the expected resource category.
        /// Currently ignored; the translator infers the category from the field's type.
        /// </summary>
        public ShaderResourceType Type { get; set; }
    }

    /// <summary>
    /// Categorizes the kind of GPU resource a <see cref="ShaderResourceAttribute"/>
    /// field represents. Reserved for future explicit-slot declarations; the
    /// translator currently infers this from the field's declared type.
    /// </summary>
    public enum ShaderResourceType
    {
        /// <summary>
        /// Typed buffer: <c>Buffer&lt;T&gt;</c> / <c>RWBuffer&lt;T&gt;</c>.
        /// </summary>
        Buffer,

        /// <summary>
        /// 2D texture: <c>Texture2D&lt;T&gt;</c> / <c>RWTexture2D&lt;T&gt;</c>.
        /// </summary>
        Texture2D,

        /// <summary>
        /// 3D texture: <c>Texture3D&lt;T&gt;</c> / <c>RWTexture3D&lt;T&gt;</c>.
        /// </summary>
        Texture3D,

        /// <summary>
        /// Sampler state.</summary>
        Sampler,

        /// <summary>
        /// Structured buffer: <c>StructuredBuffer&lt;T&gt;</c> / <c>RWStructuredBuffer&lt;T&gt;</c>.
        /// </summary>
        StructuredBuffer,
    }
}

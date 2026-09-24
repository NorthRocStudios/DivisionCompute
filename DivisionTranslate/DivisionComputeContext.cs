//
// Copyright (c) 2026 Rex Woodfield and DivisionTranslate contributors
//
// This file is part of DivisionTranslate and is subject to the terms
// of the DivisionTranslate License. See the LICENSE.txt file in the
// project root for full license terms.
//
using DivisionEngine.Graphics;
using DivisionEngine.MathLib;
using SharpGen.Runtime;
using System.Reflection;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DivisionEngine
{
    /// <summary>
    /// Owns a D3D11 device and immediate context, and provides a ComputeSharp-style
    /// "compile once, dispatch many, read back" API on top of <see cref="DivisionShaderCompiler"/>
    /// and <see cref="DivisionShader"/>.
    /// </summary>
    /// <remarks>
    /// One instance is intended to live for the lifetime of the renderer. Dispose it
    /// on shutdown; it disposes the underlying device and context.
    /// </remarks>
    public sealed class DivisionComputeContext : IDisposable
    {
        /// <summary>
        /// The underlying D3D11 device.
        /// </summary>
        public ID3D11Device Device { get; }

        /// <summary>
        /// The immediate device context used for all dispatches and readbacks.
        /// </summary>
        public ID3D11DeviceContext Context { get; }

        /// <summary>
        /// The compiler used to translate and compile shaders.
        /// </summary>
        public DivisionShaderCompiler Compiler { get; }

        /// <summary>
        /// Creates a D3D11 device at feature level 11_0 (or higher) on the default
        /// hardware adapter.
        /// </summary>
        /// <exception cref="InvalidOperationException">If device creation fails.</exception>
        public DivisionComputeContext()
        {
            Result result = D3D11.D3D11CreateDevice(
                null, DriverType.Hardware, DeviceCreationFlags.None,
                [FeatureLevel.Level_11_0],
                out ID3D11Device device, out ID3D11DeviceContext context);

            if (result.Failure)
                throw new InvalidOperationException($"D3D11 device creation failed: {result.Description}");

            Device = device;
            Context = context;
            Compiler = new DivisionShaderCompiler();
        }

        /// <summary>
        /// Reflects over a shader struct's <see cref="ShaderResourceAttribute"/> fields and
        /// assigns register slots in declaration order, matching the implicit slots
        /// D3DCompiler uses for SRVs (<c>t0</c>, <c>t1</c>, …), UAVs (<c>u0</c>, <c>u1</c>, …),
        /// and constant buffers (<c>b0</c>, <c>b1</c>, …).
        /// </summary>
        /// <param name="shaderType">The C# shader struct type.</param>
        public static IEnumerable<(string name, uint slot, string kind)> GetResourceLayout(Type shaderType)
        {
            uint srvSlot = 0, uavSlot = 0, cbSlot = 0;

            foreach (FieldInfo field in shaderType.GetFields(
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
            {
                if (!field.GetCustomAttributes().Any(a => a is ShaderResourceAttribute))
                    continue;

                string typeName = field.FieldType.Name; // e.g. "RWTexture2D`1"

                if (typeName is "RWTexture2D`1" or "RWBuffer`1" or "RWStructuredBuffer`1")
                    yield return (field.Name, uavSlot++, "UAV");
                else if (typeName is "Texture2D`1" or "Buffer`1" or "StructuredBuffer`1")
                    yield return (field.Name, srvSlot++, "SRV");
                else if (typeName is "ConstantBuffer`1")
                    yield return (field.Name, cbSlot++, "CB");
            }
        }

        /// <summary>
        /// Wraps an already-compiled shader and installs its resource layout.
        /// </summary>
        /// <param name="compilation">A successful HLSL compilation result.</param>
        /// <param name="shaderType">The corresponding C# shader struct type.</param>
        public DivisionShader CreateShader(HLSLCompilationResult compilation, Type shaderType)
        {
            var shader = DivisionShader.FromCompilation(compilation, Device, Context);
            shader.SetResourceLayout(GetResourceLayout(shaderType));
            return shader;
        }

        /// <summary>
        /// Convenience: translates, compiles, and wraps the given shader type.
        /// Throws on any failure; use the three separate calls if you want to
        /// inspect intermediate results.
        /// </summary>
        /// <exception cref="InvalidOperationException">If translation or compilation fails.</exception>
        public DivisionShader CompileShader(Type shaderType)
        {
            var translation = Compiler.TranslateShader(shaderType);
            if (!translation.IsSuccess)
                throw new InvalidOperationException(translation.ErrorMessage);

            var compilation = DivisionShaderCompiler.CompileHLSL(translation);
            if (!compilation.IsSuccess)
                throw new InvalidOperationException(compilation.DebugMessage);

            return CreateShader(compilation, shaderType);
        }

        /// <summary>
        /// Allocates a <c>R32G32B32A32_Float</c> texture with matching UAV and SRV views.
        /// </summary>
        /// <param name="width">Width in pixels; must be positive.</param>
        /// <param name="height">Height in pixels; must be positive.</param>
        public GPUTexture2D<float4> CreateRWTexture2D(int width, int height)
            => new(Device, width, height, Format.R32G32B32A32_Float);

        /// <summary>
        /// Copies the contents of a GPU texture into a CPU-side <c>float4[]</c>.
        /// </summary>
        /// <param name="tex">A texture created by <see cref="CreateRWTexture2D"/>.</param>
        /// <returns>
        /// Pixel data in <b>row-major</b> order, <b>top-left origin</b> - the same
        /// convention D3D11 uses for the source texture. Index <c>y * Width + x</c>.
        /// </returns>
        /// <remarks>
        /// Performs a <c>CopyResource</c> into a staging texture, then <c>Map</c>s and
        /// reads it. Blocks until the GPU has finished prior work, so call sparingly
        /// (once per frame at most).
        /// </remarks>
        public float4[] ReadTexture(GPUTexture2D<float4> tex)
        {
            var stagingDesc = new Texture2DDescription
            {
                Width = (uint)tex.Width,
                Height = (uint)tex.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = tex.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
            };

            using var staging = Device.CreateTexture2D(stagingDesc);
            Context.CopyResource(staging, tex.Texture);

            var result = new float4[tex.Width * tex.Height];
            var map = Context.Map(staging, 0, MapMode.Read);
            unsafe
            {
                float* src = (float*)map.DataPointer;
                uint rowFloats = map.RowPitch / sizeof(float);
                for (uint y = 0; y < tex.Height; y++)
                    for (uint x = 0; x < tex.Width; x++)
                    {
                        uint si = y * rowFloats + x * 4;
                        result[y * tex.Width + x] = new float4(
                            src[si + 0], src[si + 1], src[si + 2], src[si + 3]);
                    }
            }
            Context.Unmap(staging, 0);
            return result;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Context?.Dispose();
            Device?.Dispose();
        }
    }
}

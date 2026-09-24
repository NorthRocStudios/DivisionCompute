//
// Copyright (c) 2026 Rex Woodfield and DivisionTranslate contributors
//
// This file is part of DivisionTranslate and is subject to the terms
// of the DivisionTranslate License. See the LICENSE.txt file in the
// project root for full license terms.
//
using DivisionEngine.Graphics;
using DivisionEngine.MathLib;
using DivisionTranslate;
using SharpGen.Runtime;
using System.Reflection;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DivisionEngine
{
    /// <summary>
    /// Owns the D3D11 device/context and provides a ComputeSharp-style
    /// "compile once, dispatch many, read back" API.
    /// </summary>
    public sealed class DivisionComputeContext : IDisposable
    {
        public ID3D11Device Device { get; }
        public ID3D11DeviceContext Context { get; }
        public DivisionShaderCompiler Compiler { get; }

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

        /// <summary>Public now — Program.cs can print it.</summary>
        public static IEnumerable<(string name, uint slot, string kind)> GetResourceLayout(Type shaderType)
        {
            uint srvSlot = 0, uavSlot = 0, cbSlot = 0;

            foreach (FieldInfo field in shaderType.GetFields(
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
            {
                if (!field.GetCustomAttributes().Any(a => a is ShaderResourceAttribute))
                    continue;

                string typeName = field.FieldType.Name;

                if (typeName is "RWTexture2D`1" or "RWBuffer`1" or "RWStructuredBuffer`1")
                    yield return (field.Name, uavSlot++, "UAV");
                else if (typeName is "Texture2D`1" or "Buffer`1" or "StructuredBuffer`1")
                    yield return (field.Name, srvSlot++, "SRV");
                else if (typeName is "ConstantBuffer`1")
                    yield return (field.Name, cbSlot++, "CB");
            }
        }

        /// <summary>Wrap already-compiled bytecode into a bindable shader.</summary>
        public DivisionShader CreateShader(HLSLCompilationResult compilation, Type shaderType)
        {
            var shader = DivisionShader.FromCompilation(compilation, Device, Context);
            shader.SetResourceLayout(GetResourceLayout(shaderType));
            return shader;
        }

        /// <summary>One-shot convenience. Use the three separate calls if you want verbose output.</summary>
        public DivisionShader CompileShader(Type shaderType)
        {
            var translation = Compiler.TranslateShader(shaderType);
            if (!translation.IsSuccess)
                throw new InvalidOperationException(translation.ErrorMessage);

            var compilation = Compiler.CompileHLSL(translation);
            if (!compilation.IsSuccess)
                throw new InvalidOperationException(compilation.DebugMessage);

            return CreateShader(compilation, shaderType);
        }

        public GPUTexture2D<float4> CreateRWTexture2D(int width, int height)
            => new(Device, width, height, Format.R32G32B32A32_Float);

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

        public void Dispose() { Context?.Dispose(); Device?.Dispose(); }
    }
}

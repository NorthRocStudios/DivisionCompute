//
// Copyright (c) 2026 Rex Woodfield and DivisionCompute contributors
//
// This file is part of DivisionCompute and is subject to the terms
// of the DivisionCompute License. See the LICENSE.txt file in the
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
            ArgumentNullException.ThrowIfNull(shaderType);

            uint srvSlot = 0, uavSlot = 0;

            foreach (FieldInfo field in shaderType.GetFields(
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.GetCustomAttribute<ShaderResourceAttribute>() is null)
                    continue;

                Type fieldType = field.FieldType;

                // The translator injects a default sampler for Texture2D sampling.
                // An explicitly declared Sampler is emitted but not bound yet.
                if (fieldType == typeof(Sampler))
                    continue;

                if (!fieldType.IsGenericType)
                    throw new NotSupportedException(
                        $"Shader resource field '{field.Name}' has unsupported type '{fieldType.Name}'. " +
                        "Use Buffer<T>, RWBuffer<T>, StructuredBuffer<T>, RWStructuredBuffer<T>, " +
                        "Texture2D<T>, RWTexture2D<T>, Texture3D<T>, or RWTexture3D<T>.");

                Type definition = fieldType.GetGenericTypeDefinition();

                if (definition == typeof(RWTexture2D<>) ||
                    definition == typeof(RWTexture3D<>) ||
                    definition == typeof(RWBuffer<>) ||
                    definition == typeof(RWStructuredBuffer<>))
                {
                    yield return (field.Name, uavSlot++, "UAV");
                }
                else if (definition == typeof(Texture2D<>) ||
                         definition == typeof(Texture3D<>) ||
                         definition == typeof(Buffer<>) ||
                         definition == typeof(StructuredBuffer<>))
                {
                    yield return (field.Name, srvSlot++, "SRV");
                }
                else
                {
                    throw new NotSupportedException(
                        $"Shader resource field '{field.Name}' has unsupported type '{fieldType.Name}'. " +
                        "Use Buffer<T>, RWBuffer<T>, StructuredBuffer<T>, RWStructuredBuffer<T>, " +
                        "Texture2D<T>, RWTexture2D<T>, Texture3D<T>, or RWTexture3D<T>.");
                }
            }
        }

        /// <summary>
        /// Wraps an already-compiled shader and installs its resource layout.
        /// </summary>
        /// <param name="compilation">A successful HLSL compilation result.</param>
        /// <param name="shaderType">The corresponding C# shader struct type.</param>
        public DivisionShader CreateShader(HLSLCompilationResult compilation, Type shaderType)
        {
            // Materialize the layout first so an unsupported resource field fails
            // before a GPU shader object is created.
            var layout = GetResourceLayout(shaderType).ToArray();
            var shader = DivisionShader.FromCompilation(compilation, Device, Context);
            shader.SetResourceLayout(layout);

            // Plain fields form the parameter constant buffer at b0; every Set* call
            // writes into its CPU-side mirror and Dispatch uploads it once.
            shader.EnableUniforms(shaderType);
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
        /// Creates a read-only 2D texture using the DXGI format that matches
        /// <typeparamref name="T"/>.
        /// </summary>
        public Texture2D<T> CreateTexture2D<T>(int width, int height) where T : unmanaged
            => new(Device, width, height);

        /// <summary>
        /// Creates a read-only 2D texture with an explicit DXGI format.
        /// </summary>
        public Texture2D<T> CreateTexture2D<T>(int width, int height, Format format) where T : unmanaged
            => new(Device, width, height, format);

        /// <summary>
        /// Creates a read-write 2D texture using the DXGI format that matches
        /// <typeparamref name="T"/>.
        /// </summary>
        public RWTexture2D<T> CreateRWTexture2D<T>(int width, int height) where T : unmanaged
            => new(Device, width, height);

        /// <summary>
        /// Creates a read-write 2D texture with an explicit DXGI format.
        /// </summary>
        public RWTexture2D<T> CreateRWTexture2D<T>(int width, int height, Format format) where T : unmanaged
            => new(Device, width, height, format);

        /// <summary>
        /// Creates a read-only 3D texture using the DXGI format that matches
        /// <typeparamref name="T"/>.
        /// </summary>
        public Texture3D<T> CreateTexture3D<T>(int width, int height, int depth) where T : unmanaged
            => new(Device, width, height, depth);

        /// <summary>
        /// Creates a read-only 3D texture with an explicit DXGI format.
        /// </summary>
        public Texture3D<T> CreateTexture3D<T>(int width, int height, int depth, Format format) where T : unmanaged
            => new(Device, width, height, depth, format);

        /// <summary>
        /// Creates a read-write 3D texture using the DXGI format that matches
        /// <typeparamref name="T"/>.
        /// </summary>
        public RWTexture3D<T> CreateRWTexture3D<T>(int width, int height, int depth) where T : unmanaged
            => new(Device, width, height, depth);

        /// <summary>
        /// Creates a read-write 3D texture with an explicit DXGI format.
        /// </summary>
        public RWTexture3D<T> CreateRWTexture3D<T>(int width, int height, int depth, Format format) where T : unmanaged
            => new(Device, width, height, depth, format);

        /// <summary>
        /// Creates a read-only structured buffer and uploads <paramref name="data"/>.
        /// The buffer can be bound to a <c>StructuredBuffer&lt;T&gt;</c> shader field.
        /// </summary>
        /// <typeparam name="T">An unmanaged, sequential element struct.</typeparam>
        /// <param name="data">The initial element data. Must not be empty.</param>
        public StructuredBuffer<T> CreateStructuredBuffer<T>(ReadOnlySpan<T> data) where T : unmanaged
            => new(Device, data);

        /// <summary>
        /// Creates a zero-initialized read-write structured buffer. The buffer can be
        /// bound to an <c>RWStructuredBuffer&lt;T&gt;</c> shader field.
        /// </summary>
        /// <typeparam name="T">An unmanaged, sequential element struct.</typeparam>
        /// <param name="count">Number of elements; must be positive.</param>
        public RWStructuredBuffer<T> CreateRWStructuredBuffer<T>(int count) where T : unmanaged
            => new(Device, count);

        /// <summary>
        /// Creates a typed buffer and uploads <paramref name="data"/>. The buffer can be
        /// bound to a <c>Buffer&lt;T&gt;</c> shader field.
        /// </summary>
        /// <typeparam name="T">A scalar or vector with a matching DXGI format.</typeparam>
        /// <param name="data">The initial element data. Must not be empty.</param>
        public Buffer<T> CreateBuffer<T>(ReadOnlySpan<T> data) where T : unmanaged
            => new(Device, data);

        /// <summary>
        /// Creates a zero-initialized read-write typed buffer. The buffer can be bound
        /// to an <c>RWBuffer&lt;T&gt;</c> shader field.
        /// </summary>
        /// <typeparam name="T">A scalar or vector with a matching DXGI format.</typeparam>
        /// <param name="count">Number of elements; must be positive.</param>
        public RWBuffer<T> CreateRWBuffer<T>(int count) where T : unmanaged
            => new(Device, count);

        /// <summary>
        /// Creates a read-write typed buffer and uploads <paramref name="data"/>.
        /// </summary>
        /// <typeparam name="T">A scalar or vector with a matching DXGI format.</typeparam>
        /// <param name="data">The initial element data. Must not be empty.</param>
        public RWBuffer<T> CreateRWBuffer<T>(ReadOnlySpan<T> data) where T : unmanaged
            => new(Device, data);

        /// <summary>
        /// Creates a read-write structured buffer and uploads <paramref name="data"/>.
        /// </summary>
        /// <typeparam name="T">An unmanaged, sequential element struct.</typeparam>
        /// <param name="data">The initial element data. Must not be empty.</param>
        public RWStructuredBuffer<T> CreateRWStructuredBuffer<T>(ReadOnlySpan<T> data) where T : unmanaged
            => new(Device, data);

        /// <summary>
        /// Raised the first time <see cref="CheckDeviceLost"/> observes that the D3D11
        /// device has been removed.
        /// </summary>
        public event Action<string>? DeviceLost;

        private bool deviceLostRaised;

        /// <summary>
        /// True when D3D11 reports the device as removed.
        /// </summary>
        public bool IsDeviceLost => Device.DeviceRemovedReason.Failure;

        /// <summary>
        /// Polls the device state and raises <see cref="DeviceLost"/> once when the
        /// device has been removed. Call once per frame.
        /// </summary>
        /// <returns>True when the device is lost.</returns>
        public bool CheckDeviceLost()
        {
            Result reason = Device.DeviceRemovedReason;
            if (reason.Success) return false;

            if (!deviceLostRaised)
            {
                deviceLostRaised = true;
                DeviceLost?.Invoke(reason.Description ?? "Device removed");
            }

            return true;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Context?.Dispose();
            Device?.Dispose();
        }
    }
}

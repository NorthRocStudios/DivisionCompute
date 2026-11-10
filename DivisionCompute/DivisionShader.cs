//
// Copyright (c) 2026 Rex Woodfield and DivisionCompute contributors
//
// This file is part of DivisionCompute and is subject to the terms
// of the DivisionCompute License. See the LICENSE.txt file in the
// project root for full license terms.
//
using DivisionEngine;
using DivisionEngine.Graphics;
using DivisionEngine.MathLib;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Vortice.Direct3D11;

namespace DivisionEngine
{

    /// <summary>
    /// Runtime wrapper around a compiled D3D11 compute shader. Holds one
    /// bytecode-backed <see cref="ID3D11ComputeShader"/> per kernel, the
    /// kernel-name → index map, thread-group sizes, and the resource layout that
    /// maps C# field names to HLSL register slots.
    /// </summary>
    /// <remarks>
    /// Created via <see cref="DivisionComputeContext.CompileShader(Type)"/> or
    /// <see cref="FromCompilation"/>. Callers set resources by field name
    /// (<see cref="SetUav"/>, <see cref="SetSrv"/>, <see cref="SetConstantBuffer"/>)
    /// and then call <see cref="Dispatch"/>.
    /// </remarks>
    public partial class DivisionShader : IDisposable
    {
        /// <summary>
        /// Name of the constant buffer that holds the shader's plain fields.
        /// </summary>
        public const string UniformBufferName = "DivisionParams";

        private readonly ID3D11ComputeShader[] _computeShaders;
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _deviceContext;
        private readonly Dictionary<string, int> _kernelNameToIndex;
        private readonly Dictionary<int, (uint x, uint y, uint z)> _kernelThreadSizes;

        // Resource storage keyed by C# field name
        private readonly Dictionary<string, ID3D11Buffer> _cbuffers = [];
        private readonly Dictionary<string, ID3D11ShaderResourceView> _srvs = [];
        private readonly Dictionary<string, ID3D11UnorderedAccessView> _uavs = [];

        // Ordered list of (field name, register slot, kind) so dispatch can bind by slot
        private readonly List<(string name, uint slot, string kind)> _layout = [];

        // Shader uniforms packed into one b0 constant buffer
        private ShaderUniformLayout? _uniformLayout;
        private byte[]? _uniformBytes;
        private ID3D11Buffer? _uniformBuffer;
        private bool _uniformsDirty;

        /// <summary>
        /// Name of the originating C# shader struct.
        /// </summary>
        public string ShaderName { get; }

        /// <summary>
        /// When <c>true</c>, <see cref="Dispatch"/> writes dispatch geometry and
        /// completion notices to <see cref="Debug"/>. Defaults to <c>false</c>;
        /// enable when diagnosing performance or resource-binding issues.
        /// </summary>
        public static bool Verbose { get; set; }

        private DivisionShader(HLSLCompilationResult result, ID3D11Device device, ID3D11DeviceContext context)
        {
            ShaderName = result.ShaderTypeName;
            _device = device;
            _deviceContext = context;

            if (result.KernelBytecodes.Count == 0)
                throw new InvalidOperationException($"Compilation result for '{ShaderName}' contains no kernel bytecode.");

            if (result.KernelBytecodes.Count != result.KernelNames.Count)
                throw new InvalidOperationException(
                    $"Compilation result for '{ShaderName}' has {result.KernelBytecodes.Count} bytecode blobs " +
                    $"but {result.KernelNames.Count} kernel names.");

            _computeShaders = new ID3D11ComputeShader[result.KernelBytecodes.Count];
            for (int i = 0; i < result.KernelBytecodes.Count; i++)
            {
                byte[] bytecode = result.KernelBytecodes[i];
                if (bytecode.Length == 0)
                    throw new InvalidOperationException($"Kernel '{result.KernelNames[i]}' has an empty bytecode blob.");

                unsafe
                {
                    fixed (byte* ptr = bytecode)
                    {
                        _computeShaders[i] = device.CreateComputeShader(ptr, (nuint)bytecode.Length);
                    }
                }
            }

            (_kernelNameToIndex, _kernelThreadSizes) = ParseKernels(result.HLSLCode, result.KernelNames);
        }

        /// <summary>
        /// Wraps an already-compiled shader compilation result into a dispatcher.
        /// </summary>
        /// <param name="result">A successful result from <see cref="DivisionShaderCompiler.CompileHLSL"/>.</param>
        /// <param name="device">The D3D11 device to create the compute shader on.</param>
        /// <param name="context">The immediate context used for dispatch and readback.</param>
        /// <exception cref="InvalidOperationException">If <paramref name="result"/> is not successful.</exception>
        public static DivisionShader FromCompilation(HLSLCompilationResult result, ID3D11Device device, ID3D11DeviceContext context)
        {
            if (!result.IsSuccess) throw new InvalidOperationException($"Cannot create shader: {result.DebugMessage}");
            return new DivisionShader(result, device, context);
        }

        /// <summary>
        /// Returns the dispatch index for a kernel, or <c>-1</c> if no kernel by that name exists.
        /// </summary>
        public int FindKernel(string name)
            => _kernelNameToIndex.GetValueOrDefault(name, -1);

        /// <summary>
        /// Replaces the shader's resource layout. Called by the compile facade after
        /// construction with the output of <c>DivisionComputeContext.GetResourceLayout</c>.
        /// </summary>
        /// <param name="layout">
        /// One entry per <see cref="ShaderResourceAttribute"/> field: the field name,
        /// its HLSL register slot, and its kind (<c>"UAV"</c>, <c>"SRV"</c>, or <c>"CB"</c>).
        /// </param>
        public void SetResourceLayout(IEnumerable<(string name, uint slot, string kind)> layout)
        {
            _layout.Clear();
            _layout.AddRange(layout);
        }

        #region uniforms

        /// <summary>
        /// True when the shader declares uniforms (plain, non-<c>[ShaderResource]</c> fields).
        /// </summary>
        public bool HasUniforms => _uniformLayout is { Fields.Count: > 0 };

        /// <summary>
        /// The uniform names of this shader, in declaration order.
        /// </summary>
        public IReadOnlyList<string> UniformNames
            => _uniformLayout is null ? [] : [.. _uniformLayout.Names];

        /// <summary>
        /// Builds the parameter constant buffer that holds the shader's plain fields
        /// and binds it to <c>b0</c>. Called by <see cref="DivisionComputeContext.CreateShader"/>.
        /// </summary>
        /// <param name="shaderType">The <c>[Shader]</c> struct type.</param>
        public void EnableUniforms(Type shaderType)
        {
            ArgumentNullException.ThrowIfNull(shaderType);

            if (_layout.Any(entry => entry.kind == "CB"))
                throw new InvalidOperationException(
                    $"Shader '{ShaderName}' already has a constant buffer bound; uniforms were enabled twice.");

            _uniformLayout = ShaderUniformLayout.For(shaderType);
            if (_uniformLayout.Fields.Count == 0) return;

            _uniformBytes = new byte[_uniformLayout.ByteSize];
            _uniformBuffer = _device.CreateBuffer(
                _uniformBytes,
                BindFlags.ConstantBuffer,
                ResourceUsage.Default,
                CpuAccessFlags.None,
                ResourceOptionFlags.None,
                sizeInBytes: checked((uint)_uniformBytes.Length),
                structureByteStride: 0);

            _cbuffers[UniformBufferName] = _uniformBuffer;
            _layout.Add((UniformBufferName, 0, "CB"));
        }

        /// <summary>
        /// Sets a <c>float</c> uniform by name.
        /// </summary>
        /// <param name="name">The shader struct field name.</param>
        /// <param name="value">The value to upload on the next dispatch.</param>
        public void SetFloat(string name, float value) => WriteUniform(name, UniformKind.Float, value);

        /// <summary>
        /// Sets an <c>int</c> uniform by name.
        /// </summary>
        /// <param name="name">The shader struct field name.</param>
        /// <param name="value">The value to upload on the next dispatch.</param>
        public void SetInt(string name, int value) => WriteUniform(name, UniformKind.Int, value);

        /// <summary>
        /// Sets a <c>uint</c> uniform by name.
        /// </summary>
        /// <param name="name">The shader struct field name.</param>
        /// <param name="value">The value to upload on the next dispatch.</param>
        public void SetUInt(string name, uint value) => WriteUniform(name, UniformKind.UInt, value);

        /// <summary>
        /// Sets a <c>bool</c> uniform by name. Written as a 32-bit 0/1 flag, which is
        /// how HLSL stores bools in a constant buffer.
        /// </summary>
        /// <param name="name">The shader struct field name.</param>
        /// <param name="value">The value to upload on the next dispatch.</param>
        public void SetBool(string name, bool value) => WriteUniform(name, UniformKind.Bool, value);

        /// <summary>Sets a <c>float2</c> uniform by name.</summary>
        public void SetVector(string name, float2 value) => WriteUniform(name, UniformKind.Float2, value);

        /// <summary>Sets a <c>float3</c> uniform by name.</summary>
        public void SetVector(string name, float3 value) => WriteUniform(name, UniformKind.Float3, value);

        /// <summary>Sets a <c>float4</c> uniform by name.</summary>
        public void SetVector(string name, float4 value) => WriteUniform(name, UniformKind.Float4, value);

        /// <summary>Sets an <c>int2</c> uniform by name.</summary>
        public void SetVector(string name, int2 value) => WriteUniform(name, UniformKind.Int2, value);

        /// <summary>Sets an <c>int3</c> uniform by name.</summary>
        public void SetVector(string name, int3 value) => WriteUniform(name, UniformKind.Int3, value);

        /// <summary>Sets an <c>int4</c> uniform by name.</summary>
        public void SetVector(string name, int4 value) => WriteUniform(name, UniformKind.Int4, value);

        /// <summary>Sets a <c>uint2</c> uniform by name.</summary>
        public void SetVector(string name, uint2 value) => WriteUniform(name, UniformKind.UInt2, value);

        /// <summary>Sets a <c>uint3</c> uniform by name.</summary>
        public void SetVector(string name, uint3 value) => WriteUniform(name, UniformKind.UInt3, value);

        /// <summary>Sets a <c>uint4</c> uniform by name.</summary>
        public void SetVector(string name, uint4 value) => WriteUniform(name, UniformKind.UInt4, value);

        /// <summary>
        /// Sets a uniform by name from any compatible value, such as an enum.
        /// The value's size must match the uniform's HLSL type.
        /// </summary>
        /// <typeparam name="T">An unmanaged value type.</typeparam>
        /// <param name="name">The shader struct field name.</param>
        /// <param name="value">The value to upload on the next dispatch.</param>
        public void Set<T>(string name, in T value) where T : unmanaged
        {
            UniformField field = RequireField(name);
            ShaderUniformLayout.Write(UniformSpan(), in field, in value);
            _uniformsDirty = true;
        }

        /// <summary>
        /// Returns a copy of the packed uniform bytes in the HLSL layout. Intended for
        /// tests and diagnostics.
        /// </summary>
        public byte[] GetUniformData()
            => (byte[])(_uniformBytes
                ?? throw new InvalidOperationException($"Shader '{ShaderName}' has no uniforms.")).Clone();

        private void WriteUniform<T>(string name, UniformKind kind, in T value) where T : unmanaged
        {
            UniformField field = RequireField(name);
            if (field.Kind != kind)
                throw new ArgumentException(
                    $"Uniform '{name}' on shader '{ShaderName}' is {ShaderUniformLayout.GetHlslTypeName(field.Kind)}; " +
                    $"{DescribeSetter(field.Kind)} expects that type.",
                    nameof(name));

            ShaderUniformLayout.Write(UniformSpan(), in field, in value);
            _uniformsDirty = true;
        }

        private Span<byte> UniformSpan()
            => (_uniformBytes
                ?? throw new InvalidOperationException(
                    $"Shader '{ShaderName}' has no uniforms. Add plain fields to the shader struct to declare them."))
                .AsSpan();

        private UniformField RequireField(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);

            ShaderUniformLayout layout = _uniformLayout
                ?? throw new InvalidOperationException(
                    $"Shader '{ShaderName}' has no uniforms. Add plain fields to the shader struct to declare them.");

            if (!layout.TryGet(name, out UniformField field))
                throw new ArgumentException(
                    $"Shader '{ShaderName}' has no uniform named '{name}'. Known uniforms: {DescribeUniforms(layout)}.",
                    nameof(name));

            return field;
        }

        private static string DescribeSetter(UniformKind kind) => kind switch
        {
            UniformKind.Float => "SetFloat",
            UniformKind.Int => "SetInt",
            UniformKind.UInt => "SetUInt",
            UniformKind.Bool => "SetBool",
            _ => $"SetVector with a {ShaderUniformLayout.GetHlslTypeName(kind)}",
        };

        private static string DescribeUniforms(ShaderUniformLayout layout)
            => layout.Fields.Count == 0
                ? "(none)"
                : string.Join(", ", layout.Fields.Select(f => $"{f.Name} ({ShaderUniformLayout.GetHlslTypeName(f.Kind)})"));

        #endregion uniforms

        /// <summary>
        /// The resource bindings of this shader, in declaration order: the C# field
        /// name, its HLSL register slot, and its kind (<c>"UAV"</c>, <c>"SRV"</c>, or
        /// <c>"CB"</c>). Tooling uses this to discover what a shader needs bound.
        /// </summary>
        public IReadOnlyList<(string name, uint slot, string kind)> ResourceLayout => _layout;

        /// <summary>
        /// Returns the thread-group size declared by the kernel's <c>[numthreads]</c>
        /// attribute, so callers can compute dispatch group counts without
        /// duplicating the kernel attribute.
        /// </summary>
        /// <param name="kernelIndex">Index returned by <see cref="FindKernel"/>.</param>
        /// <exception cref="ArgumentException">If <paramref name="kernelIndex"/> is not valid.</exception>
        public (uint x, uint y, uint z) GetThreadGroupSize(int kernelIndex)
        {
            if (!_kernelThreadSizes.TryGetValue(kernelIndex, out var sizes))
                throw new ArgumentException($"Invalid kernel index: {kernelIndex}", nameof(kernelIndex));

            return sizes;
        }

        /// <summary>
        /// Dispatches a 2D kernel over <paramref name="width"/> x <paramref name="height"/>
        /// threads, rounding the thread-group counts up. Equivalent to ComputeSharp's
        /// <c>GraphicsDevice.For(width, height, shader)</c>.
        /// </summary>
        /// <param name="kernelIndex">Index returned by <see cref="FindKernel"/>.</param>
        /// <param name="width">Total threads along X.</param>
        /// <param name="height">Total threads along Y.</param>
        public void Dispatch2D(int kernelIndex, int width, int height)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "Dispatch width must be positive.");
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "Dispatch height must be positive.");

            (uint x, uint y, uint z) = GetThreadGroupSize(kernelIndex);
            uint groupsX = (uint)((width + (int)x - 1) / (int)x);
            uint groupsY = (uint)((height + (int)y - 1) / (int)y);
            Dispatch(kernelIndex, Math.Max(1u, groupsX), Math.Max(1u, groupsY), Math.Max(1u, z));
        }

        /// <summary>
        /// Binds an unordered-access view to the register slot of the named field.
        /// </summary>
        public void SetUav(string fieldName, ID3D11UnorderedAccessView uav)
        {
            ArgumentNullException.ThrowIfNull(uav);
            ValidateBinding(fieldName, "UAV");
            _uavs[fieldName] = uav;
        }

        /// <summary>
        /// Binds a shader-resource view to the register slot of the named field.
        /// </summary>
        public void SetSrv(string fieldName, ID3D11ShaderResourceView srv)
        {
            ArgumentNullException.ThrowIfNull(srv);
            ValidateBinding(fieldName, "SRV");
            _srvs[fieldName] = srv;
        }

        /// <summary>
        /// Binds a constant buffer to the register slot of the named field.
        /// </summary>
        public void SetConstantBuffer(string fieldName, ID3D11Buffer buffer)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ValidateBinding(fieldName, "CB");
            _cbuffers[fieldName] = buffer;
        }

        /// <summary>
        /// Binds a <see cref="StructuredBuffer{T}"/> to a <c>StructuredBuffer&lt;T&gt;</c>
        /// shader field.
        /// </summary>
        public void SetStructuredBuffer<T>(string fieldName, StructuredBuffer<T> buffer) where T : unmanaged
        {
            ArgumentNullException.ThrowIfNull(buffer);
            SetSrv(fieldName, buffer.Srv);
        }

        /// <summary>
        /// Binds the read view of an <see cref="RWStructuredBuffer{T}"/> to a
        /// <c>StructuredBuffer&lt;T&gt;</c> shader field.
        /// </summary>
        public void SetStructuredBuffer<T>(string fieldName, RWStructuredBuffer<T> buffer) where T : unmanaged
        {
            ArgumentNullException.ThrowIfNull(buffer);
            SetSrv(fieldName, buffer.Srv);
        }

        /// <summary>
        /// Binds an <see cref="RWStructuredBuffer{T}"/> to an
        /// <c>RWStructuredBuffer&lt;T&gt;</c> shader field.
        /// </summary>
        public void SetRWStructuredBuffer<T>(string fieldName, RWStructuredBuffer<T> buffer) where T : unmanaged
        {
            ArgumentNullException.ThrowIfNull(buffer);
            SetUav(fieldName, buffer.Uav);
        }

        /// <summary>
        /// Binds a <see cref="Texture2D{T}"/> to a <c>Texture2D&lt;T&gt;</c>
        /// shader field.
        /// </summary>
        public void SetTexture2D<T>(string fieldName, Texture2D<T> texture) where T : unmanaged
        {
            ArgumentNullException.ThrowIfNull(texture);
            SetSrv(fieldName, texture.Srv);
        }

        /// <summary>
        /// Binds the read view of an <see cref="RWTexture2D{T}"/> to a
        /// <c>Texture2D&lt;T&gt;</c> shader field.
        /// </summary>
        public void SetTexture2D<T>(string fieldName, RWTexture2D<T> texture) where T : unmanaged
        {
            ArgumentNullException.ThrowIfNull(texture);
            SetSrv(fieldName, texture.Srv);
        }

        /// <summary>
        /// Binds an <see cref="RWTexture2D{T}"/> to an <c>RWTexture2D&lt;T&gt;</c>
        /// shader field.
        /// </summary>
        public void SetRWTexture2D<T>(string fieldName, RWTexture2D<T> texture) where T : unmanaged
        {
            ArgumentNullException.ThrowIfNull(texture);
            SetUav(fieldName, texture.Uav);
        }

        /// <summary>
        /// Binds a <see cref="Texture3D{T}"/> to a <c>Texture3D&lt;T&gt;</c>
        /// shader field.
        /// </summary>
        public void SetTexture3D<T>(string fieldName, Texture3D<T> texture) where T : unmanaged
        {
            ArgumentNullException.ThrowIfNull(texture);
            SetSrv(fieldName, texture.Srv);
        }

        /// <summary>
        /// Binds the read view of an <see cref="RWTexture3D{T}"/> to a
        /// <c>Texture3D&lt;T&gt;</c> shader field.
        /// </summary>
        public void SetTexture3D<T>(string fieldName, RWTexture3D<T> texture) where T : unmanaged
        {
            ArgumentNullException.ThrowIfNull(texture);
            SetSrv(fieldName, texture.Srv);
        }

        /// <summary>
        /// Binds an <see cref="RWTexture3D{T}"/> to an <c>RWTexture3D&lt;T&gt;</c>
        /// shader field.
        /// </summary>
        public void SetRWTexture3D<T>(string fieldName, RWTexture3D<T> texture) where T : unmanaged
        {
            ArgumentNullException.ThrowIfNull(texture);
            SetUav(fieldName, texture.Uav);
        }

        /// <summary>
        /// Binds a <see cref="Buffer{T}"/> to a <c>Buffer&lt;T&gt;</c> shader field.
        /// </summary>
        public void SetBuffer<T>(string fieldName, Buffer<T> buffer) where T : unmanaged
        {
            ArgumentNullException.ThrowIfNull(buffer);
            SetSrv(fieldName, buffer.Srv);
        }

        /// <summary>
        /// Binds the read view of an <see cref="RWBuffer{T}"/> to a <c>Buffer&lt;T&gt;</c>
        /// shader field.
        /// </summary>
        public void SetBuffer<T>(string fieldName, RWBuffer<T> buffer) where T : unmanaged
        {
            ArgumentNullException.ThrowIfNull(buffer);
            SetSrv(fieldName, buffer.Srv);
        }

        /// <summary>
        /// Binds an <see cref="RWBuffer{T}"/> to an <c>RWBuffer&lt;T&gt;</c> shader field.
        /// </summary>
        public void SetRWBuffer<T>(string fieldName, RWBuffer<T> buffer) where T : unmanaged
        {
            ArgumentNullException.ThrowIfNull(buffer);
            SetUav(fieldName, buffer.Uav);
        }

        /// <summary>
        /// Clears the shader's resource slots, binds all previously-set resources,
        /// dispatches the selected kernel, and unbinds UAVs so that a subsequent
        /// readback copy can succeed.
        /// </summary>
        /// <param name="kernelIndex">Index returned by <see cref="FindKernel"/>.</param>
        /// <param name="threadGroupsX">Number of thread groups along X.</param>
        /// <param name="threadGroupsY">Number of thread groups along Y.</param>
        /// <param name="threadGroupsZ">Number of thread groups along Z.</param>
        /// <exception cref="ArgumentException">If <paramref name="kernelIndex"/> is not valid.</exception>
        public void Dispatch(int kernelIndex, uint threadGroupsX, uint threadGroupsY, uint threadGroupsZ)
        {
            if (!_kernelThreadSizes.TryGetValue(kernelIndex, out var sizes))
                throw new ArgumentException($"Invalid kernel index: {kernelIndex}");

            // Upload the packed uniforms once per dispatch, before the buffer is
            // bound. This mirrors Unity: many Set* calls, one constant-buffer update.
            if (_uniformBuffer is not null && _uniformsDirty && _uniformBytes is not null)
            {
                _deviceContext.UpdateSubresource<byte>(_uniformBytes, _uniformBuffer);
                _uniformsDirty = false;
            }

            if (Verbose)
            {
                Debug.WriteLine($"Dispatching kernel {kernelIndex}");
                Debug.WriteLine($"  Thread groups: {threadGroupsX}, {threadGroupsY}, {threadGroupsZ}");
                Debug.WriteLine($"  Threads per group: {sizes.x}, {sizes.y}, {sizes.z}");
                Debug.WriteLine($"  Total threads: {threadGroupsX * sizes.x}, {threadGroupsY * sizes.y}, {threadGroupsZ * sizes.z}");
            }

            _deviceContext.CSSetShader(_computeShaders[kernelIndex]);

            // Clear every slot used by this shader before binding. Otherwise a
            // resource omitted from this dispatch could remain bound from the
            // previous one, which makes bugs depend on dispatch order.
            ClearResourceSlots();

            foreach (var (name, slot, kind) in _layout)
            {
                switch (kind)
                {
                    case "UAV":
                        if (_uavs.TryGetValue(name, out var uav))
                            _deviceContext.CSSetUnorderedAccessView(slot, uav);
                        break;
                    case "SRV":
                        if (_srvs.TryGetValue(name, out var srv))
                            _deviceContext.CSSetShaderResource(slot, srv);
                        break;
                    case "CB":
                        if (_cbuffers.TryGetValue(name, out var cb))
                            _deviceContext.CSSetConstantBuffer(slot, cb);
                        break;
                }
            }

            _deviceContext.Dispatch(threadGroupsX, threadGroupsY, threadGroupsZ);
            _deviceContext.Flush();

            // Release every slot after the dispatch. In particular, D3D11 refuses
            // to copy a resource that is still bound as a UAV, and clearing SRVs
            // keeps a later SetData from touching a still-bound resource.
            ClearResourceSlots();

            if (Verbose) Debug.WriteLine("  Dispatch complete");
        }

        /// <summary>
        /// Releases every register slot used by this shader's resource layout.
        /// </summary>
        private void ClearResourceSlots()
        {
            foreach (var (_, slot, kind) in _layout)
            {
                switch (kind)
                {
                    case "UAV":
                        _deviceContext.CSSetUnorderedAccessView(slot, (ID3D11UnorderedAccessView)null!);
                        break;
                    case "SRV":
                        _deviceContext.CSSetShaderResource(slot, (ID3D11ShaderResourceView)null!);
                        break;
                    case "CB":
                        _deviceContext.CSSetConstantBuffer(slot, (ID3D11Buffer)null!);
                        break;
                }
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _uniformBuffer?.Dispose();

            foreach (ID3D11ComputeShader shader in _computeShaders)
                shader?.Dispose();
        }

        /// <summary>
        /// Verifies that a field exists in the resource layout and has the expected
        /// kind. A layout-less shader (created through <see cref="FromCompilation"/>
        /// without <see cref="SetResourceLayout"/>) skips this check.
        /// </summary>
        private void ValidateBinding(string fieldName, string expectedKind)
        {
            if (string.IsNullOrWhiteSpace(fieldName))
                throw new ArgumentException("A resource field name is required.", nameof(fieldName));

            if (_layout.Count == 0)
                return;

            foreach (var (name, _, kind) in _layout)
            {
                if (!string.Equals(name, fieldName, StringComparison.Ordinal))
                    continue;

                if (!string.Equals(kind, expectedKind, StringComparison.Ordinal))
                    throw new ArgumentException(
                        $"Shader '{ShaderName}' field '{fieldName}' is a {kind} resource, not {expectedKind}.",
                        nameof(fieldName));

                return;
            }

            throw new ArgumentException(
                $"Shader '{ShaderName}' has no resource field named '{fieldName}'.",
                nameof(fieldName));
        }

        private static (Dictionary<string, int> nameToIndex, Dictionary<int, (uint x, uint y, uint z)> threadSizes)
            ParseKernels(string hlslCode, List<string> kernelNames)
        {
            var nameToIndex = new Dictionary<string, int>();
            var threadSizes = new Dictionary<int, (uint, uint, uint)>();
            var numthreadsRegex = ParseKernelsRegex();
            var lines = hlslCode.Split('\n');

            for (int idx = 0; idx < kernelNames.Count; idx++)
            {
                var kernelName = kernelNames[idx];
                uint x = 64, y = 1, z = 1;

                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains($"void {kernelName}("))
                    {
                        for (int j = i - 1; j >= 0 && j >= i - 5; j--)
                        {
                            var match = numthreadsRegex.Match(lines[j]);
                            if (match.Success)
                            {
                                x = uint.Parse(match.Groups[1].Value);
                                y = uint.Parse(match.Groups[2].Value);
                                z = uint.Parse(match.Groups[3].Value);
                                break;
                            }
                        }
                        break;
                    }
                }

                nameToIndex[kernelName] = idx;
                threadSizes[idx] = (x, y, z);
            }

            return (nameToIndex, threadSizes);
        }

        [GeneratedRegex(@"\[numthreads\((\d+),\s*(\d+),\s*(\d+)\)\]")]
        private static partial Regex ParseKernelsRegex();
    }
}

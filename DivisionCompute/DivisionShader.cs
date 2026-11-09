//
// Copyright (c) 2026 Rex Woodfield and DivisionCompute contributors
//
// This file is part of DivisionCompute and is subject to the terms
// of the DivisionCompute License. See the LICENSE.txt file in the
// project root for full license terms.
//
using DivisionEngine;
using DivisionEngine.Graphics;
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
        private readonly ID3D11ComputeShader[] _computeShaders;
        private readonly ID3D11DeviceContext _deviceContext;
        private readonly Dictionary<string, int> _kernelNameToIndex;
        private readonly Dictionary<int, (uint x, uint y, uint z)> _kernelThreadSizes;

        // Resource storage keyed by C# field name
        private readonly Dictionary<string, ID3D11Buffer> _cbuffers = [];
        private readonly Dictionary<string, ID3D11ShaderResourceView> _srvs = [];
        private readonly Dictionary<string, ID3D11UnorderedAccessView> _uavs = [];

        // Ordered list of (field name, register slot, kind) so dispatch can bind by slot
        private readonly List<(string name, uint slot, string kind)> _layout = [];

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
        /// Binds a <see cref="ReadOnlyBuffer{T}"/> to a <c>StructuredBuffer&lt;T&gt;</c>
        /// shader field.
        /// </summary>
        public void SetReadOnlyBuffer<T>(string fieldName, ReadOnlyBuffer<T> buffer) where T : unmanaged
        {
            ArgumentNullException.ThrowIfNull(buffer);
            SetSrv(fieldName, buffer.Srv);
        }

        /// <summary>
        /// Binds the read view of a <see cref="ReadWriteBuffer{T}"/> to a
        /// <c>StructuredBuffer&lt;T&gt;</c> shader field.
        /// </summary>
        public void SetReadOnlyBuffer<T>(string fieldName, ReadWriteBuffer<T> buffer) where T : unmanaged
        {
            ArgumentNullException.ThrowIfNull(buffer);
            SetSrv(fieldName, buffer.Srv);
        }

        /// <summary>
        /// Binds a <see cref="ReadWriteBuffer{T}"/> to an
        /// <c>RWStructuredBuffer&lt;T&gt;</c> shader field.
        /// </summary>
        public void SetReadWriteBuffer<T>(string fieldName, ReadWriteBuffer<T> buffer) where T : unmanaged
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

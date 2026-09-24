//
// Copyright (c) 2026 Rex Woodfield and DivisionTranslate contributors
//
// This file is part of DivisionTranslate and is subject to the terms
// of the DivisionTranslate License. See the LICENSE.txt file in the
// project root for full license terms.
//
namespace DivisionEngine
{
    ///// <summary>
    ///// Represents a compiled compute shader, mirroring the Unity ComputeShader API.
    ///// </summary>
    //public class DivisionShader : IDisposable
    //{
    //    // --- Core D3D11 Objects ---
    //    private readonly ID3D11Device _device;
    //    private readonly ID3D11DeviceContext _context;
    //    private readonly ID3D11ComputeShader _computeShader;
    //    private readonly Dictionary<int, KernelInfo> _kernelIndexMap;
    //    private readonly Dictionary<string, int> _kernelNameMap;
    //    private readonly Dictionary<int, Dictionary<string, ResourceBinding>> _kernelResources;

    //    // Constant buffer management (global across all kernels)
    //    private readonly Dictionary<uint, ID3D11Buffer> _constantBuffers;
    //    private uint _nextConstantBufferSlot = 0;

    //    // Resource slot management
    //    private uint _nextSRVSlot = 0;
    //    private uint _nextUAVSlot = 0;

    //    public string Name { get; }
    //    public IReadOnlyList<string> KernelNames => _kernelNameMap.Keys.ToList().AsReadOnly();

    //    private DivisionShader(HLSLCompilationResult result, ID3D11Device device, ID3D11DeviceContext context)
    //    {
    //        _device = device;
    //        _context = context;
    //        Name = result.ShaderTypeName;
    //        _constantBuffers = [];

    //        // 1. Create the native D3D11 compute shader from the bytecode
    //        unsafe
    //        {
    //            fixed (byte* ptr = result.Bytecode)
    //            {
    //                _computeShader = device.CreateComputeShader(ptr, (nuint)result.Bytecode!.Length);
    //            }
    //        }

    //        // 2. Parse the HLSL source code to find all kernels
    //        (_kernelIndexMap, _kernelNameMap) = ParseKernels(result.HLSLCode, result.KernelNames);

    //        // 3. Initialize resource tracking per kernel
    //        _kernelResources = new Dictionary<int, Dictionary<string, ResourceBinding>>();
    //        foreach (var kernelIndex in _kernelIndexMap.Keys)
    //        {
    //            _kernelResources[kernelIndex] = new Dictionary<string, ResourceBinding>();
    //        }
    //    }

    //    public static DivisionShader FromCompilation(HLSLCompilationResult result, ID3D11Device device, ID3D11DeviceContext context)
    //    {
    //        if (!result.IsSuccess)
    //            throw new InvalidOperationException($"Cannot create shader from failed compilation: {result.DebugMessage}");
    //        return new DivisionShader(result, device, context);
    //    }

    //    #region Unity-style Public API

    //    public int FindKernel(string name)
    //    {
    //        return _kernelNameMap.GetValueOrDefault(name, -1);
    //    }

    //    public bool HasKernel(string name) => _kernelNameMap.ContainsKey(name);

    //    public void GetKernelThreadGroupSizes(int kernelIndex, out uint x, out uint y, out uint z)
    //    {
    //        if (!_kernelIndexMap.TryGetValue(kernelIndex, out var kernelInfo))
    //            throw new ArgumentException($"Kernel with index {kernelIndex} not found.");
    //        x = kernelInfo.ThreadsX;
    //        y = kernelInfo.ThreadsY;
    //        z = kernelInfo.ThreadsZ;
    //    }

    //    public void Dispatch(int kernelIndex, int threadGroupsX, int threadGroupsY, int threadGroupsZ)
    //    {
    //        if (!_kernelIndexMap.ContainsKey(kernelIndex))
    //            throw new ArgumentException($"Kernel with index {kernelIndex} not found.");

    //        // Set the shader
    //        _context.CSSetShader(_computeShader);

    //        // Bind all resources for this kernel
    //        BindAllResourcesForKernel(kernelIndex);

    //        // Bind all constant buffers (global across all kernels)
    //        BindAllConstantBuffers();

    //        // Execute
    //        _context.Dispatch((uint)threadGroupsX, (uint)threadGroupsY, (uint)threadGroupsZ);

    //        // Unbind UAVs to allow CPU readback (important!)
    //        UnbindAllUAVsForKernel(kernelIndex);
    //    }

    //    public void SetBuffer(int kernelIndex, string name, ID3D11Buffer buffer)
    //    {
    //        var binding = GetOrCreateBinding(kernelIndex, name);

    //        // Determine if this is read-only or read-write based on buffer type
    //        // For simplicity, assume Buffer = SRV, RWBuffer = UAV
    //        if (binding.ShaderResourceView == null && binding.UnorderedAccessView == null)
    //        {
    //            // Create appropriate view
    //            var bufferDesc = buffer.Description;
    //            if ((bufferDesc.BindFlags & BindFlags.ShaderResource) != 0)
    //            {
    //                binding.ShaderResourceView = _device.CreateShaderResourceView(buffer);
    //                binding.Slot = _nextSRVSlot++;
    //            }
    //            else if ((bufferDesc.BindFlags & BindFlags.UnorderedAccess) != 0)
    //            {
    //                binding.UnorderedAccessView = _device.CreateUnorderedAccessView(buffer);
    //                binding.Slot = _nextUAVSlot++;
    //            }
    //        }

    //        binding.Buffer = buffer;
    //    }

    //    public void SetBuffer<T>(int kernelIndex, string name, T[] data) where T : struct
    //    {
    //        // Create buffer from data
    //        var elementSize = Marshal.SizeOf<T>();
    //        var bufferDesc = new BufferDescription
    //        {
    //            ByteWidth = (uint)(elementSize * data.Length),
    //            BindFlags = BindFlags.ShaderResource, // Default to read-only
    //            Usage = ResourceUsage.Default
    //        };

    //        var buffer = _device.CreateBuffer(bufferDesc, data);
    //        SetBuffer(kernelIndex, name, buffer);
    //    }

    //    public void SetRWBuffer<T>(int kernelIndex, string name, T[] data) where T : struct
    //    {
    //        // Create read-write buffer from data
    //        var elementSize = Marshal.SizeOf<T>();
    //        var bufferDesc = new BufferDescription
    //        {
    //            ByteWidth = (uint)(elementSize * data.Length),
    //            BindFlags = BindFlags.UnorderedAccess,
    //            Usage = ResourceUsage.Default
    //        };

    //        var buffer = _device.CreateBuffer(bufferDesc, data);

    //        var binding = GetOrCreateBinding(kernelIndex, name);
    //        binding.UnorderedAccessView = _device.CreateUnorderedAccessView(buffer);
    //        binding.Slot = _nextUAVSlot++;
    //        binding.Buffer = buffer;
    //    }

    //    public void SetFloat(int kernelIndex, string name, float value)
    //    {
    //        // Create or update constant buffer for this value
    //        // We pack all floats into a single constant buffer per shader for efficiency
    //        SetConstant(kernelIndex, name, value);
    //    }

    //    public void SetInt(int kernelIndex, string name, int value)
    //    {
    //        SetConstant(kernelIndex, name, value);
    //    }

    //    public void SetVector(int kernelIndex, string name, float x, float y, float z, float w)
    //    {
    //        SetConstant(kernelIndex, name, new Vector4(x, y, z, w));
    //    }

    //    public void SetMatrix(int kernelIndex, string name, float[] matrix) // 4x4 = 16 floats
    //    {
    //        if (matrix.Length != 16)
    //            throw new ArgumentException("Matrix must be 4x4 (16 floats)");
    //        SetConstant(kernelIndex, name, matrix);
    //    }

    //    #endregion

    //    #region Private Implementation

    //    private class KernelInfo
    //    {
    //        public uint ThreadsX, ThreadsY, ThreadsZ;
    //    }

    //    private class ResourceBinding
    //    {
    //        public string Name;
    //        public Type Type;
    //        public uint Slot;
    //        public ID3D11Buffer? Buffer;
    //        public ID3D11ShaderResourceView? ShaderResourceView;
    //        public ID3D11UnorderedAccessView? UnorderedAccessView;
    //    }

    //    private class ConstantValue
    //    {
    //        public object Value;
    //        public uint Offset;
    //        public uint Size;
    //    }

    //    private ResourceBinding GetOrCreateBinding(int kernelIndex, string name)
    //    {
    //        var bindings = _kernelResources[kernelIndex];
    //        if (!bindings.TryGetValue(name, out var binding))
    //        {
    //            binding = new ResourceBinding { Name = name };
    //            bindings[name] = binding;
    //        }
    //        return binding;
    //    }

    //    private void SetConstant(int kernelIndex, string name, object value)
    //    {
    //        // For simplicity, we create a dedicated constant buffer for each value
    //        // In production, you'd want to pack them efficiently
    //        var size = value switch
    //        {
    //            float => 4,
    //            int => 4,
    //            Vector4 => 16,
    //            float[] arr => arr.Length * 4,
    //            _ => Marshal.SizeOf(value.GetType())
    //        };

    //        var bufferDesc = new BufferDescription
    //        {
    //            ByteWidth = (uint)size,
    //            BindFlags = BindFlags.ConstantBuffer,
    //            Usage = ResourceUsage.Default
    //        };

    //        var buffer = _device.CreateBuffer(bufferDesc, value);
    //        var binding = GetOrCreateBinding(kernelIndex, name);
    //        binding.Buffer = buffer;
    //        binding.Type = typeof(ConstantBufferDescription);
    //        binding.Slot = _nextConstantBufferSlot++;

    //        // Store for global binding
    //        _constantBuffers[binding.Slot] = buffer;
    //    }

    //    private void BindAllResourcesForKernel(int kernelIndex)
    //    {
    //        foreach (var binding in _kernelResources[kernelIndex].Values)
    //        {
    //            if (binding.ShaderResourceView != null)
    //            {
    //                _context.CSSetShaderResource(binding.Slot, binding.ShaderResourceView);
    //            }
    //            else if (binding.UnorderedAccessView != null)
    //            {
    //                _context.CSSetUnorderedAccessView(binding.Slot, binding.UnorderedAccessView);
    //            }
    //        }
    //    }

    //    private void BindAllConstantBuffers()
    //    {
    //        foreach (var (slot, buffer) in _constantBuffers)
    //        {
    //            _context.CSSetConstantBuffer(slot, buffer);
    //        }
    //    }

    //    private void UnbindAllUAVsForKernel(int kernelIndex)
    //    {
    //        foreach (var binding in _kernelResources[kernelIndex].Values)
    //        {
    //            if (binding.UnorderedAccessView != null)
    //            {
    //                _context.CSSetUnorderedAccessView(binding.Slot, null);
    //            }
    //        }
    //    }

    //    private (Dictionary<int, KernelInfo> indexMap, Dictionary<string, int> nameMap) ParseKernels(string hlslCode, List<string> kernelNames)
    //    {
    //        var indexMap = new Dictionary<int, KernelInfo>();
    //        var nameMap = new Dictionary<string, int>();
    //        var numthreadsRegex = new Regex(@"\[numthreads\((\d+),\s*(\d+),\s*(\d+)\)\]");
    //        var lines = hlslCode.Split('\n');

    //        Console.WriteLine($"\n=== Parsing Kernels from HLSL ===");
    //        Console.WriteLine($"Found {kernelNames.Count} kernel names: {string.Join(", ", kernelNames)}");
    //        Console.WriteLine($"HLSL has {lines.Length} lines");

    //        for (int idx = 0; idx < kernelNames.Count; idx++)
    //        {
    //            var kernelName = kernelNames[idx];
    //            uint x = 64, y = 1, z = 1;
    //            bool found = false;

    //            for (int i = 0; i < lines.Length; i++)
    //            {
    //                var line = lines[i];
    //                if (line.Contains($"void {kernelName}("))
    //                {
    //                    Console.WriteLine($"\nFound kernel '{kernelName}' at line {i}: {line.Trim()}");

    //                    // Search backwards up to 10 lines for numthreads
    //                    for (int j = i - 1; j >= 0 && j >= i - 10; j--)
    //                    {
    //                        var match = numthreadsRegex.Match(lines[j]);
    //                        if (match.Success)
    //                        {
    //                            x = uint.Parse(match.Groups[1].Value);
    //                            y = uint.Parse(match.Groups[2].Value);
    //                            z = uint.Parse(match.Groups[3].Value);
    //                            Console.WriteLine($"  Found [numthreads({x},{y},{z})] at line {j}");
    //                            found = true;
    //                            break;
    //                        }
    //                    }
    //                    if (!found)
    //                    {
    //                        Console.WriteLine($"  No [numthreads] found, using defaults ({x},{y},{z})");
    //                    }
    //                    break;
    //                }
    //            }

    //            indexMap[idx] = new KernelInfo { ThreadsX = x, ThreadsY = y, ThreadsZ = z };
    //            nameMap[kernelName] = idx;
    //            Console.WriteLine($"  Kernel {idx}: {kernelName} -> Threads({x},{y},{z})");
    //        }
    //        Console.WriteLine($"=== End Kernel Parsing ===\n");

    //        return (indexMap, nameMap);
    //    }

    //    #endregion

    //    #region Readback Helpers

    //    public T[] ReadBuffer<T>(ID3D11Buffer buffer, int count) where T : struct
    //    {
    //        // Create staging buffer for CPU readback
    //        var stagingDesc = new BufferDescription
    //        {
    //            ByteWidth = (uint)(Marshal.SizeOf<T>() * count),
    //            Usage = ResourceUsage.Staging,
    //            CPUAccessFlags = CpuAccessFlags.Read
    //        };

    //        var stagingBuffer = _device.CreateBuffer(stagingDesc);
    //        _context.CopyResource(stagingBuffer, buffer);

    //        // Map and read
    //        var mapped = _context.Map(stagingBuffer, 0, MapMode.Read);
    //        var result = new T[count];
    //        var ptr = mapped.DataPointer;

    //        for (int i = 0; i < count; i++)
    //        {
    //            result[i] = Marshal.PtrToStructure<T>(ptr + (i * Marshal.SizeOf<T>()));
    //        }

    //        _context.Unmap(stagingBuffer, 0);
    //        stagingBuffer.Dispose();

    //        return result;
    //    }

    //    #endregion

    //    public void Dispose()
    //    {
    //        _computeShader?.Dispose();
    //        foreach (var buffer in _constantBuffers.Values)
    //            buffer?.Dispose();
    //        foreach (var kernelResources in _kernelResources.Values)
    //        {
    //            foreach (var binding in kernelResources.Values)
    //            {
    //                binding.Buffer?.Dispose();
    //                binding.ShaderResourceView?.Dispose();
    //                binding.UnorderedAccessView?.Dispose();
    //            }
    //        }
    //        _constantBuffers.Clear();
    //        _kernelResources.Clear();
    //    }
    //}

    //// Helper struct for vectors
    //public struct Vector4(float x, float y, float z, float w)
    //{
    //    public float x = x, y = y, z = z, w = w;
    //}
}

using System.Text.RegularExpressions;
using Vortice.Direct3D11;

namespace DivisionTranslate
{

    public partial class DivisionShader : IDisposable
    {
        private readonly ID3D11ComputeShader _computeShader;
        private readonly ID3D11DeviceContext _deviceContext;
        private readonly Dictionary<string, int> _kernelNameToIndex;
        private readonly Dictionary<int, (uint x, uint y, uint z)> _kernelThreadSizes;

        // resource storage + slot layout
        private readonly Dictionary<string, ID3D11Buffer> _cbuffers = [];
        private readonly Dictionary<string, ID3D11ShaderResourceView> _srvs = [];
        private readonly Dictionary<string, ID3D11UnorderedAccessView> _uavs = [];
        private readonly List<(string name, uint slot, string kind)> _layout = [];

        public string ShaderName { get; }

        private DivisionShader(HLSLCompilationResult result, ID3D11Device device, ID3D11DeviceContext context)
        {
            ShaderName = result.ShaderTypeName;
            _deviceContext = context;

            // Create the native compute shader from bytecode
            unsafe
            {
                fixed (byte* ptr = result.Bytecode)
                {
                    _computeShader = device.CreateComputeShader(ptr, (nuint)result.Bytecode!.Length);
                }
            }

            // Parse kernels from HLSL source
            (_kernelNameToIndex, _kernelThreadSizes) = ParseKernels(result.HLSLCode, result.KernelNames);
        }

        public static DivisionShader FromCompilation(HLSLCompilationResult result, ID3D11Device device, ID3D11DeviceContext context)
        {
            if (!result.IsSuccess)
                throw new InvalidOperationException($"Cannot create shader: {result.DebugMessage}");
            return new DivisionShader(result, device, context);
        }

        public int FindKernel(string name)
        {
            return _kernelNameToIndex.GetValueOrDefault(name, -1);
        }

        /// <summary>
        /// Called by the facade after construction. Each entry describes one
        /// [ShaderResource] field: its name, its register slot, and its kind.
        /// </summary>
        public void SetResourceLayout(IEnumerable<(string name, uint slot, string kind)> layout)
        {
            _layout.Clear();
            _layout.AddRange(layout);
        }

        public void SetUav(string fieldName, ID3D11UnorderedAccessView uav) => _uavs[fieldName] = uav;
        public void SetSrv(string fieldName, ID3D11ShaderResourceView srv) => _srvs[fieldName] = srv;
        public void SetConstantBuffer(string fieldName, ID3D11Buffer buffer) => _cbuffers[fieldName] = buffer;

        public void Dispatch(int kernelIndex, uint threadGroupsX, uint threadGroupsY, uint threadGroupsZ)
        {
            if (!_kernelThreadSizes.TryGetValue(kernelIndex, out var sizes))
                throw new ArgumentException($"Invalid kernel index: {kernelIndex}");

            Console.WriteLine($"Dispatching kernel {kernelIndex}");
            Console.WriteLine($"  Thread groups: {threadGroupsX}, {threadGroupsY}, {threadGroupsZ}");
            Console.WriteLine($"  Threads per group: {sizes.x}, {sizes.y}, {sizes.z}");
            Console.WriteLine($"  Total threads: {threadGroupsX * sizes.x}, {threadGroupsY * sizes.y}, {threadGroupsZ * sizes.z}");

            _deviceContext.CSSetShader(_computeShader);

            // Bind everything by field name → slot.
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

            // CRITICAL: unbind UAVs, otherwise the staging copy in readback
            // silently fails (D3D11 refuses to copy a resource that is still
            // bound as a UAV).
            foreach (var (_, slot, kind) in _layout)
                if (kind == "UAV")
                    _deviceContext.CSSetUnorderedAccessView(slot, (ID3D11UnorderedAccessView)null!);

            Console.WriteLine("  Dispatch complete");
        }

        public void Dispose()
        {
            _computeShader?.Dispose();
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

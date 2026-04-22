using Vortice.Direct3D11;

namespace DivisionTranslate.ReferenceClasses
{
    public class ShaderCompilationResult
    {
        public bool Success { get; set; }
        public string HLSLCode { get; set; }
        public string ErrorMessage { get; set; }
        public Dictionary<string, byte[]> ShaderBytecodes { get; set; } = new Dictionary<string, byte[]>();

        // Convenience method to get shader bytecode as ReadOnlyMemory
        public ReadOnlyMemory<byte> GetBytecode(string shaderName)
        {
            if (ShaderBytecodes.TryGetValue(shaderName, out var bytecode))
                return new ReadOnlyMemory<byte>(bytecode);

            return null;
        }

        // Create compute shader from bytecode (for Vortice.Direct3D11)
        public ID3D11ComputeShader CreateComputeShader(ID3D11Device device, string shaderName)
        {
            if (!ShaderBytecodes.TryGetValue(shaderName, out var bytecode))
                throw new ArgumentException($"Shader '{shaderName}' not found");

            return device.CreateComputeShader(bytecode);
        }
    }
}

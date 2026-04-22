namespace DivisionTranslate
{
    /// <summary>
    /// Represents a compiled HLSL shader.
    /// </summary>
    public class HLSLCompilationResult
    {
        /// <summary>
        /// Was the HLSL compilation successful?
        /// </summary>
        public bool IsSuccess { get; }

        /// <summary>
        /// HLSL source code.
        /// </summary>
        public string HLSLCode { get; }

        /// <summary>
        /// If failed to compile, error message.
        /// </summary>
        public string ErrorMessage { get; }

        /// <summary>
        /// Name of each compiled kernel.
        /// </summary>
        public List<string> KernelNames { get; }

        /// <summary>
        /// Shader type name.
        /// </summary>
        public string ShaderTypeName { get; }

        /// <summary>
        /// Compiled bytecode.
        /// </summary>
        public byte[]? Bytecode { get; }

        private HLSLCompilationResult(bool isSuccess, string hlslCode, string errorMessage,
                               List<string> kernelNames, string shaderTypeName, byte[]? bytecode)
        {
            IsSuccess = isSuccess;
            HLSLCode = hlslCode;
            ErrorMessage = errorMessage;
            KernelNames = kernelNames ?? [];
            ShaderTypeName = shaderTypeName;
            Bytecode = bytecode;
        }

        /// <summary>
        /// Build a successful HLSLCompilationResult container.
        /// </summary>
        /// <param name="hlslCode">HLSL source code</param>
        /// <param name="kernelNames">Names of each compiled kernel</param>
        /// <param name="shaderTypeName">Shader type name</param>
        /// <param name="bytecode">The compiled HLSL bytecode</param>
        /// <returns>Successful HLSL compilation container</returns>
        public static HLSLCompilationResult Success(string hlslCode, List<string> kernelNames,
                                              string shaderTypeName, byte[] bytecode) =>
            new HLSLCompilationResult(true, hlslCode, string.Empty, kernelNames, shaderTypeName, bytecode);

        /// <summary>
        /// Build a failed HLSLCompilationResult container.
        /// </summary>
        /// <param name="errorMessage">Error message of the compilation failure</param>
        /// <param name="shaderTypeName">Shader type name</param>
        /// <returns>Failed HLSL compilation container</returns>
        public static HLSLCompilationResult Failure(string errorMessage, string shaderTypeName = "Unknown") => 
            new HLSLCompilationResult(false, string.Empty, errorMessage, new List<string>(), shaderTypeName, null);
    }
}

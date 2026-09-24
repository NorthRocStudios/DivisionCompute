//
// Copyright (c) 2026 Rex Woodfield and DivisionCompute contributors
//
// This file is part of DivisionCompute and is subject to the terms
// of the DivisionCompute License. See the LICENSE.txt file in the
// project root for full license terms.
//
namespace DivisionEngine
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
        /// Compiler debug output message.
        /// </summary>
        public string DebugMessage { get; }

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

        private HLSLCompilationResult(bool isSuccess, string hlslCode, string debugMessage,
                               List<string> kernelNames, string shaderTypeName, byte[]? bytecode)
        {
            IsSuccess = isSuccess;
            HLSLCode = hlslCode;
            DebugMessage = debugMessage;
            KernelNames = kernelNames ?? [];
            ShaderTypeName = shaderTypeName;
            Bytecode = bytecode;
        }

        /// <summary>
        /// Build a successful HLSLCompilationResult container.
        /// </summary>
        /// <param name="hlslCode">HLSL source code</param>
        /// <param name="kernelNames">Names of each compiled kernel</param>
        /// <param name="debugMessage">Debug output of the compiler</param>
        /// <param name="shaderTypeName">Shader type name</param>
        /// <param name="bytecode">The compiled HLSL bytecode</param>
        /// <returns>Successful HLSL compilation container</returns>
        public static HLSLCompilationResult Success(string hlslCode, List<string> kernelNames, string debugMessage, string shaderTypeName, byte[] bytecode) =>
            new HLSLCompilationResult(true, hlslCode, debugMessage, kernelNames, shaderTypeName, bytecode);

        /// <summary>
        /// Build a failed HLSLCompilationResult container.
        /// </summary>
        /// <param name="debugMessage">Debug message of the compilation failure</param>
        /// <param name="shaderTypeName">Shader type name</param>
        /// <returns>Failed HLSL compilation container</returns>
        public static HLSLCompilationResult Failure(string debugMessage, string shaderTypeName = "Unknown") => 
            new HLSLCompilationResult(false, string.Empty, debugMessage, [], shaderTypeName, null);
    }
}

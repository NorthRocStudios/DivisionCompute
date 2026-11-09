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
        /// Compiled bytecode for the first kernel. Kept for compatibility; use
        /// <see cref="KernelBytecodes"/> when the shader contains multiple kernels.
        /// </summary>
        public byte[]? Bytecode => KernelBytecodes.Count > 0 ? KernelBytecodes[0] : null;

        /// <summary>
        /// Compiled DXBC bytecode for every kernel, in the same order as
        /// <see cref="KernelNames"/>.
        /// </summary>
        public IReadOnlyList<byte[]> KernelBytecodes { get; }

        private HLSLCompilationResult(
            bool isSuccess,
            string hlslCode,
            string debugMessage,
            List<string> kernelNames,
            string shaderTypeName,
            IReadOnlyList<byte[]> kernelBytecodes)
        {
            IsSuccess = isSuccess;
            HLSLCode = hlslCode;
            DebugMessage = debugMessage;
            KernelNames = kernelNames ?? [];
            ShaderTypeName = shaderTypeName;
            KernelBytecodes = kernelBytecodes;
        }

        /// <summary>
        /// Build a successful HLSLCompilationResult container for a single kernel.
        /// </summary>
        /// <param name="hlslCode">HLSL source code</param>
        /// <param name="kernelNames">Names of each compiled kernel</param>
        /// <param name="debugMessage">Debug output of the compiler</param>
        /// <param name="shaderTypeName">Shader type name</param>
        /// <param name="bytecode">The compiled HLSL bytecode</param>
        /// <returns>Successful HLSL compilation container</returns>
        public static HLSLCompilationResult Success(
            string hlslCode,
            List<string> kernelNames,
            string debugMessage,
            string shaderTypeName,
            byte[] bytecode)
            => Success(hlslCode, kernelNames, debugMessage, shaderTypeName, new[] { bytecode });

        /// <summary>
        /// Build a successful HLSLCompilationResult container for one or more kernels.
        /// </summary>
        /// <param name="hlslCode">HLSL source code</param>
        /// <param name="kernelNames">Names of each compiled kernel</param>
        /// <param name="debugMessage">Debug output of the compiler</param>
        /// <param name="shaderTypeName">Shader type name</param>
        /// <param name="kernelBytecodes">Compiled bytecode for each kernel, in kernel order</param>
        /// <returns>Successful HLSL compilation container</returns>
        public static HLSLCompilationResult Success(
            string hlslCode,
            List<string> kernelNames,
            string debugMessage,
            string shaderTypeName,
            IReadOnlyList<byte[]> kernelBytecodes)
        {
            ArgumentNullException.ThrowIfNull(hlslCode);
            ArgumentNullException.ThrowIfNull(kernelNames);
            ArgumentNullException.ThrowIfNull(kernelBytecodes);

            if (kernelNames.Count != kernelBytecodes.Count)
                throw new ArgumentException(
                    $"Received {kernelBytecodes.Count} kernel bytecode blobs for {kernelNames.Count} kernel names.",
                    nameof(kernelBytecodes));

            if (kernelBytecodes.Count == 0)
                throw new ArgumentException("At least one kernel bytecode is required.", nameof(kernelBytecodes));

            return new HLSLCompilationResult(
                true,
                hlslCode,
                debugMessage,
                kernelNames,
                shaderTypeName,
                kernelBytecodes);
        }

        /// <summary>
        /// Build a failed HLSLCompilationResult container.
        /// </summary>
        /// <param name="debugMessage">Debug message of the compilation failure</param>
        /// <param name="shaderTypeName">Shader type name</param>
        /// <returns>Failed HLSL compilation container</returns>
        public static HLSLCompilationResult Failure(string debugMessage, string shaderTypeName = "Unknown")
            => new(false, string.Empty, debugMessage, [], shaderTypeName, []);
    }
}

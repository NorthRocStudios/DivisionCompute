//
// Copyright (c) 2026 Rex Woodfield and DivisionTranslate contributors
//
// This file is part of DivisionTranslate and is subject to the terms
// of the DivisionTranslate License. See the LICENSE.txt file in the
// project root for full license terms.
//
namespace DivisionTranslate
{
    /// <summary>
    /// Represents the result of compiling a C# shader to HLSL
    /// </summary>
    public class HLSLTranslationResult
    {
        /// <summary>
        /// Was the shader compilation a success or failure?
        /// </summary>
        public bool IsSuccess { get; }

        /// <summary>
        /// Compiled HLSL code if success.
        /// </summary>
        public string? HLSLCode { get; }

        /// <summary>
        /// Error message if failure to compile.
        /// </summary>
        public string? ErrorMessage { get; }

        /// <summary>
        /// Name of each translated shader kernel.
        /// </summary>
        public List<string> KernelNames { get; }

        /// <summary>
        /// Name of the containing shader type.
        /// </summary>
        public string ShaderTypeName { get; }

        private HLSLTranslationResult(bool success, string? hlslCode, string? errorMessage,
                                   List<string> kernelNames, string shaderTypeName)
        {
            IsSuccess = success;
            HLSLCode = hlslCode;
            ErrorMessage = errorMessage;
            KernelNames = kernelNames ?? [];
            ShaderTypeName = shaderTypeName;
        }

        /// <summary>
        /// Create a successful compilation result.
        /// </summary>
        /// <param name="hlslCode">Compiled HLSL code</param>
        /// <param name="kernelNames">Name of each translated kernel</param>
        /// <param name="shaderTypeName">Shader type name</param>
        /// <returns>Compilation result container</returns>
        public static HLSLTranslationResult Success(string hlslCode, List<string> kernelNames, string shaderTypeName) => 
            new HLSLTranslationResult(true, hlslCode, null, kernelNames, shaderTypeName);

        /// <summary>
        /// Create a failed compilation result.
        /// </summary>
        /// <param name="errorMessage">Error message for compilation failure</param>
        /// <param name="shaderTypeName">Shader type name if available</param>
        /// <returns>Compilation result container</returns>
        public static HLSLTranslationResult Failure(string errorMessage, string shaderTypeName = "Unknown") => 
            new HLSLTranslationResult(false, null, errorMessage, [], shaderTypeName);

        /// <summary>
        /// Compilation result string.
        /// </summary>
        /// <returns>Compiled result string if success, error message if failure</returns>
        public override string ToString()
        {
            if (IsSuccess) return $"Compilation successful. HLSL size: {HLSLCode?.Length ?? 0} characters";
            else return $"Compilation failed: {ErrorMessage}";
        }
    }
}

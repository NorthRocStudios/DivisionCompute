namespace DivisionTranslate
{
    /// <summary>
    /// Represents the result of compiling a C# shader to HLSL
    /// </summary>
    public class CompiledDivisionShader
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

        private CompiledDivisionShader(bool success, string? hlslCode, string? errorMessage)
        {
            IsSuccess = success;
            HLSLCode = hlslCode;
            ErrorMessage = errorMessage;
        }

        /// <summary>
        /// Create a successful compilation result.
        /// </summary>
        /// <param name="hlslCode">Compiled HLSL code</param>
        /// <returns>Compilation result container</returns>
        public static CompiledDivisionShader Success(string hlslCode) => new CompiledDivisionShader(true, hlslCode, null);

        /// <summary>
        /// Create a failed compilation result.
        /// </summary>
        /// <param name="errorMessage">Error message for compilation failure</param>
        /// <returns>Compilation result container</returns>
        public static CompiledDivisionShader Failure(string errorMessage) => new CompiledDivisionShader(false, null, errorMessage);

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

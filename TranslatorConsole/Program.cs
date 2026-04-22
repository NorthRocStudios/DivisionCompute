using DivisionTranslate;
using DivisionTranslate.TestShaders;

// Compile test shaders
DivisionShaderCompiler compiler = new DivisionShaderCompiler();
List<CompiledDivisionShader> compiledShaders =
[
    //// Basic math shader tests
    //compiler.CompileShader(typeof(MyShader)),
    //compiler.CompileShader(typeof(BasicMathShader)),
    //compiler.CompileShader(typeof(AdvancedMathShader)),
    //compiler.CompileShader(typeof(SDFOperationsShader)),
    //compiler.CompileShader(typeof(ComplexExpressionsShader)),

    //// Advanced math shader tests
    //compiler.CompileShader(typeof(ComplexControlFlowShader)),
    //compiler.CompileShader(typeof(TypeInferenceShader)),
    //compiler.CompileShader(typeof(ComplexExpressionsShaderAdv)),

    //// Resources tests
    //compiler.CompileShader(typeof(ResourceTestShader)),
    //compiler.CompileShader(typeof(ResourceTestShader2)),

    // Struct and resource tests
    compiler.CompileShader(typeof(StructTest2Shader)),
    compiler.CompileShader(typeof(MultiKernelTestShader)),
    compiler.CompileShader(typeof(StructTest4Shader)),
];

foreach (CompiledDivisionShader shaderResult in compiledShaders)
{
    if (shaderResult.IsSuccess)
    {
        // Use the HLSL code
        Console.WriteLine($"\n\nCompiled Size: {shaderResult.HLSLCode?.Length} bytes");
        Console.WriteLine($"Shader code:\n\n{shaderResult.HLSLCode}");

        //// Compile with Vortice and create compute shader
        //var bytecode = Vortice.D3DCompiler.Compiler.Compile(result.HLSLCode, "CSMain", "cs_5_0");
        //var shader = device.CreateComputeShader(bytecode);

        //// Dispatch!
        //context.Dispatch(64, 1, 1);
    }
    else
    {
        // Show error in editor UI
        Console.WriteLine($"Shader compilation failed: {shaderResult.ErrorMessage}");
    }
}


//
// Copyright (c) 2026 Rex Woodfield and DivisionTranslate contributors
//
// This file is part of DivisionTranslate and is subject to the terms
// of the DivisionTranslate License. See the LICENSE.txt file in the
// project root for full license terms.
//
using DivisionEngine.MathLib;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SharpGen.Runtime;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Vortice.D3DCompiler;
using Vortice.Direct3D;

namespace DivisionTranslate
{
    public class DivisionShaderCompiler
    {
        private readonly List<MetadataReference> assemblyRefs;

        public DivisionShaderCompiler()
        {
            assemblyRefs = // Add necessary assemblies
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(float3).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(ShaderAttribute).Assembly.Location),
                //MetadataReference.CreateFromFile(typeof(Graphics.Buffer<>).Assembly.Location),
            ];
        }

        /// <summary>
        /// Helper function that translates a shader type to HLSL and then compiles to DXIL bytecode.
        /// </summary>
        public HLSLCompilationResult CompileShader(Type shaderType)
        {
            // STranslate C# to HLSL
            HLSLTranslationResult translationResult = TranslateShader(shaderType);
            if (!translationResult.IsSuccess)
                return HLSLCompilationResult.Failure(translationResult.ErrorMessage ?? "No error message provided");

            // Compile HLSL to bytecode (all kernels in one shader)
            HLSLCompilationResult compileResult = CompileHLSL(translationResult);
            return compileResult;
        }

        /// <summary>
        /// Compiles HLSL source code to bytecode using the D3D compiler.
        /// </summary>
        public HLSLCompilationResult CompileHLSL(HLSLTranslationResult translatedShader)
        {
            // Use the overload that returns separate code and error blobs
            Result compileResult = Compiler.Compile(
                translatedShader.HLSLCode!,
                translatedShader.KernelNames.First(),
                translatedShader.ShaderTypeName,
                "cs_5_0",
                out Blob? codeBlob,
                out Blob? errorBlob
            );

            string debugOutput = string.Empty; // Check for errors or warnings
            if (errorBlob != null && errorBlob.AsBytes().Length > 0)
                debugOutput = Encoding.UTF8.GetString(errorBlob.AsBytes()) ?? compileResult.Description ?? "No Additional Compilation Info";

            if (compileResult.Failure) // Compilation failed
            {
                Debug.WriteLine("----------------------------------------");
                Debug.WriteLine($"Division Shader Compiler:\nFailed to compile \"{translatedShader.ShaderTypeName}\"\n\n{debugOutput}");
                Debug.WriteLine("----------------------------------------");

                errorBlob?.Dispose();
                codeBlob?.Dispose();
                return HLSLCompilationResult.Failure(debugOutput, translatedShader.ShaderTypeName);
            }

            // Compilation succeeded, get the bytecode
            byte[] bytecode = codeBlob!.AsBytes();
            Debug.WriteLine("----------------------------------------");
            Debug.WriteLine($"Division Shader Compiler:\nCompiled \"{translatedShader.ShaderTypeName}\", size: {bytecode.Length} bytes");
            if (!string.IsNullOrEmpty(debugOutput)) Debug.WriteLine(debugOutput);
            Debug.WriteLine("----------------------------------------");

            codeBlob.Dispose();
            errorBlob?.Dispose();
            return HLSLCompilationResult.Success(
                translatedShader.HLSLCode!,
                translatedShader.KernelNames,
                debugOutput,
                translatedShader.ShaderTypeName,
                bytecode
            );

            // DXC compiler, for the future:

            //dxcCompilerOptions = new DxcCompilerOptions
            //{
            //    ShaderModel = DXCShaderModel,
            //    OptimizationLevel = OptimizationLevel,
            //    EnableDebugInfo = EnableDebugInfo,
            //    WarningsAreErrors = false,
            //    SkipValidation = false,
            //};

            //List<string> additionalArgs =
            //[
            //    "-Wall",
            //];

            //IDxcResult dxcResult = DxcCompiler.Compile(
            //    DxcShaderStage.Compute,
            //    translatedShader.HLSLCode!,
            //    translatedShader.KernelNames.First(),
            //    dxcCompilerOptions,
            //    additionalArguments: [.. additionalArgs]
            //);

            //Result compilerStatus = dxcResult.GetStatus();
            //if (compilerStatus.Success)
            //{
            //    if (dxcResult.HasOutput(DxcOutKind.Object))
            //    {
            //        // Compilation succeeded, get the bytecode
            //        string debugOutput = compilerStatus.Description ?? "No compiler output";
            //        if (dxcResult.HasOutput(DxcOutKind.Errors))
            //        {
            //            IDxcBlob debugBlob = dxcResult.GetOutput(DxcOutKind.Errors);
            //            if (debugBlob.AsBytes().Length > 0) debugOutput = Encoding.UTF8.GetString(debugBlob.AsBytes());
            //            debugBlob.Dispose();
            //        }

            //        // Get compiled bytecode and output compilation
            //        byte[] bytecode = dxcResult.GetObjectBytecode().ToArray();
            //        Debug.WriteLine("----------------------------------------");
            //        Debug.WriteLine($"Division Shader Compiler:\nCompiled \"" +
            //            $"{translatedShader.ShaderTypeName}\", size: {bytecode.Length} bytes\n\n{debugOutput}");
            //        Debug.WriteLine("----------------------------------------");
            //        dxcResult.Dispose();
            //        return HLSLCompilationResult.Success(
            //            translatedShader.HLSLCode!,
            //            translatedShader.KernelNames,
            //            debugOutput,
            //            translatedShader.ShaderTypeName,
            //            bytecode
            //        );
            //    }
            //    else
            //    {
            //        dxcResult.Dispose();
            //        return HLSLCompilationResult.Failure("No output object from DXC compiler", translatedShader.ShaderTypeName);
            //    }
            //}
            //else
            //{
            //    // Compilation failed, get error messages
            //    string errors = compilerStatus.Description ?? "Unknown compilation error";
            //    if (dxcResult.HasOutput(DxcOutKind.Errors))
            //    {
            //        IDxcBlob errorBlob = dxcResult.GetOutput(DxcOutKind.Errors);
            //        if (errorBlob.AsBytes().Length > 0) errors = Encoding.UTF8.GetString(errorBlob.AsBytes());
            //        errorBlob.Dispose();
            //    }
            //    Debug.WriteLine("----------------------------------------");
            //    Debug.WriteLine($"Division Shader Compiler:\nFailed to compile \"{translatedShader.ShaderTypeName}\"\n\n{errors}");
            //    Debug.WriteLine("----------------------------------------");

            //    dxcResult.Dispose();
            //    return HLSLCompilationResult.Failure(errors, translatedShader.ShaderTypeName);
            //}
        }

        public HLSLTranslationResult TranslateShader(Type shaderType)
        {
            // Get the source file path
            string sourcePath = GetSourceFilePath(shaderType);
            if (!File.Exists(sourcePath)) return HLSLTranslationResult.Failure($"Source file not found: {sourcePath}");

            // Read and parse with Roslyn
            string sourceCode = File.ReadAllText(sourcePath);
            SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(sourceCode, path: sourcePath);

            // Create compilation with proper references
            CSharpCompilation compilation = CSharpCompilation.Create($"{shaderType.Name}_Compilation")
                .AddSyntaxTrees(syntaxTree)
                .AddReferences(assemblyRefs);

            // Get semantic model and struct declaration that matches shader type
            SemanticModel semanticModel = compilation.GetSemanticModel(syntaxTree);
            SyntaxNode root = syntaxTree.GetRoot();
            StructDeclarationSyntax? structDeclaration = FindStructDeclaration(root, shaderType.Name);
            if (structDeclaration == null) return HLSLTranslationResult.Failure($"Struct '{shaderType.Name}' not found in source file");

            // Get ALL kernel names from the struct
            List<string> kernelNames = FindAllKernelNames(structDeclaration, semanticModel);
            if (kernelNames.Count == 0) return HLSLTranslationResult.Failure($"No [Kernel] methods found in struct '{shaderType.Name}'", shaderType.Name);

            // Translate using C# Roslyn semantic walker
            DivisionTranslator translator = new DivisionTranslator(semanticModel);
            string hlslCode = translator.Translate(structDeclaration);
            return HLSLTranslationResult.Success(hlslCode, kernelNames, shaderType.Name);
        }

        private static StructDeclarationSyntax? FindStructDeclaration(SyntaxNode root, string structName) => root.DescendantNodes()
                .OfType<StructDeclarationSyntax>()
                .FirstOrDefault(structDecl => structDecl.Identifier.Text == structName);

        private static List<string> FindAllKernelNames(StructDeclarationSyntax structDeclaration, SemanticModel semanticModel)
        {
            List<string> kernelNames = [];
            foreach (MethodDeclarationSyntax method in structDeclaration.Members.OfType<MethodDeclarationSyntax>())
            {
                IMethodSymbol? methodSymbol = semanticModel.GetDeclaredSymbol(method);
                if (methodSymbol?.GetAttributes().Any(attr => attr.AttributeClass?.Name == "KernelAttribute") == true)
                    kernelNames.Add(method.Identifier.Text);
            }
            return kernelNames;
        }

        // This needs to have the relative path fixed.
        private static string GetSourceFilePath(Type shaderType)
        {
            // Try attribute path first
            ShaderAttribute? shaderAttr = shaderType.GetCustomAttribute<ShaderAttribute>();
            if (!string.IsNullOrEmpty(shaderAttr?.SourceFilePath))
            {
                // Handle relative paths
                if (Path.IsPathRooted(shaderAttr.SourceFilePath)) return shaderAttr.SourceFilePath;
                else return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, shaderAttr.SourceFilePath);
            }

            // Fallback: convention-based directory name
            string? assemblyDir = Path.GetDirectoryName(shaderType.Assembly.Location);
            string expectedPath = Path.Combine(assemblyDir ?? string.Empty, "Shaders", $"{shaderType.Name}.cs");
            return expectedPath;
        }
    }
}

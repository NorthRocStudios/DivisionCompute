using DivisionEngine.MathLib;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SharpGen.Runtime;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Vortice.Dxc;

namespace DivisionTranslate
{
    public class DivisionShaderCompiler
    {
        public DxcShaderModel DXCShaderModel { get; set; } = DxcShaderModel.Model6_0;
        public bool EnableDebugInfo { get; set; } = true;
        public int OptimizationLevel { get; set; } = 3;

        private readonly List<MetadataReference> assemblyRefs;
        private DxcCompilerOptions dxcCompilerOptions;

        public DivisionShaderCompiler()
        {
            assemblyRefs = // Add necessary assemblies
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(float3).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(ShaderAttribute).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Graphics.Buffer<>).Assembly.Location),
            ];
            dxcCompilerOptions = new DxcCompilerOptions
            {
                ShaderModel = DXCShaderModel,
                OptimizationLevel = OptimizationLevel,
                EnableDebugInfo = EnableDebugInfo,
                WarningsAreErrors = false,
                SkipValidation = false,
            };
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
        /// Compiles HLSL source code to DXIL bytecode using DXC.
        /// </summary>
        public HLSLCompilationResult CompileHLSL(HLSLTranslationResult translatedShader)
        {
            dxcCompilerOptions = new DxcCompilerOptions
            {
                ShaderModel = DXCShaderModel,
                OptimizationLevel = OptimizationLevel,
                EnableDebugInfo = EnableDebugInfo,
                WarningsAreErrors = false,
                SkipValidation = false,
            };

            var args = new List<string>
            {
                "-E", translatedShader.KernelNames.First(),
                "-T", "cs_6_0",
                "-O" + OptimizationLevel.ToString(),
                "-Wall",                      // Enable all warnings
                "-Wunused-variable",          // Unused variable warnings
                "-Wunused-function",          // Unused function warnings  
                "-Wunreachable-code",         // Unreachable code warnings
                "-Wconversion",               // Conversion warnings (already on by default)
                "-Wsign-compare",             // Signed/unsigned comparison warnings
            };

            if (EnableDebugInfo)
            {
                args.Add("-Zi");
                args.Add("-Qembed_debug");
            }

            // DXC compiles the entire shader at once, all kernels are included
            IDxcResult dxcResult = DxcCompiler.Compile(
                translatedShader.HLSLCode!,
                args.ToArray()
            );

            Result compilerStatus = dxcResult.GetStatus();
            if (compilerStatus.Success)
            {
                if (dxcResult.HasOutput(DxcOutKind.Object))
                {
                    // Compilation succeeded, get the bytecode
                    string debugOutput = compilerStatus.Description ?? "No compiler output";
                    if (dxcResult.HasOutput(DxcOutKind.Errors))
                    {
                        IDxcBlob debugBlob = dxcResult.GetOutput(DxcOutKind.Errors);
                        Debug.WriteLine($"Debug blob size: {debugBlob.AsBytes().Length}");
                        if (debugBlob.AsBytes().Length > 0) debugOutput = Encoding.UTF8.GetString(debugBlob.AsBytes());
                        debugBlob.Dispose();
                    }

                    // Get compiled bytecode and output compilation
                    byte[] bytecode = dxcResult.GetObjectBytecode().ToArray();
                    Debug.WriteLine("----------------------------------------");
                    Debug.WriteLine($"Division Shader Compiler:\nCompiled \"" +
                        $"{translatedShader.ShaderTypeName}\", size: {bytecode.Length} bytes\n\n{debugOutput}");
                    Debug.WriteLine("----------------------------------------");
                    dxcResult.Dispose();
                    return HLSLCompilationResult.Success(
                        translatedShader.HLSLCode!,
                        translatedShader.KernelNames,
                        debugOutput,
                        translatedShader.ShaderTypeName,
                        bytecode
                    );
                }
                else
                {
                    dxcResult.Dispose();
                    return HLSLCompilationResult.Failure("No output object from DXC compiler", translatedShader.ShaderTypeName);
                }
            }
            else
            {
                // Compilation failed, get error messages
                string errors = compilerStatus.Description ?? "Unknown compilation error";
                if (dxcResult.HasOutput(DxcOutKind.Errors))
                {
                    IDxcBlob errorBlob = dxcResult.GetOutput(DxcOutKind.Errors);
                    if (errorBlob.AsBytes().Length > 0) errors = Encoding.UTF8.GetString(errorBlob.AsBytes());
                    errorBlob.Dispose();
                }
                Debug.WriteLine("----------------------------------------");
                Debug.WriteLine($"Division Shader Compiler:\nFailed to compile \"{translatedShader.ShaderTypeName}\"\n\n{errors}");
                Debug.WriteLine("----------------------------------------");

                dxcResult.Dispose();
                return HLSLCompilationResult.Failure(errors, translatedShader.ShaderTypeName);
            }
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

using DivisionEngine.MathLib;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;
using System.Runtime.InteropServices;
using Vortice.Dxc;

namespace DivisionTranslate
{
    public class DivisionShaderCompiler
    {
        public DxcShaderModel DXCShaderModel { get; set; } = DxcShaderModel.Model6_0;

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
                OptimizationLevel = 3,
                EnableDebugInfo = false,
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
            // DXC compiles the entire shader at once, all kernels are included
            IDxcResult dxcResult = DxcCompiler.Compile(
                DxcShaderStage.Compute,
                translatedShader.HLSLCode!,
                translatedShader.KernelNames.First(), // Entry point (first kernel)
                dxcCompilerOptions
            );

            // Check for compilation errors
            if (dxcResult.HasOutput(DxcOutKind.Errors))
            {
                IDxcBlob errorBlob = dxcResult.GetOutput(DxcOutKind.Errors);

                // Convert error blob to string using AsBytes() or AsSpan()
                string errors = System.Text.Encoding.UTF8.GetString(errorBlob.AsBytes());
                dxcResult.Dispose();
                errorBlob.Dispose();
                return HLSLCompilationResult.Failure(errors, translatedShader.ShaderTypeName);
            }

            // Get the compiled bytecode
            if (dxcResult.HasOutput(DxcOutKind.Object))
            {
                IDxcBlob blob = dxcResult.GetOutput(DxcOutKind.Object);

                // Use the AsBytes() helper method from Vortice
                byte[] bytecode = blob.AsBytes();
                dxcResult.Dispose();
                blob.Dispose();

                return HLSLCompilationResult.Success(
                    translatedShader.HLSLCode!,
                    translatedShader.KernelNames,
                    translatedShader.ShaderTypeName,
                    bytecode
                );
            }

            dxcResult.Dispose();
            return HLSLCompilationResult.Failure("No output from DXC compiler", translatedShader.ShaderTypeName);
        }

        public HLSLTranslationResult TranslateShader(Type shaderType)
        {
            // Get the source file path
            string sourcePath = GetSourceFilePath(shaderType);
            if (!File.Exists(sourcePath))
                return HLSLTranslationResult.Failure($"Source file not found: {sourcePath}");

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
            if (structDeclaration == null)
                return HLSLTranslationResult.Failure($"Struct '{shaderType.Name}' not found in source file");

            // Get ALL kernel names from the struct
            List<string> kernelNames = FindAllKernelNames(structDeclaration, semanticModel);
            if (kernelNames.Count == 0)
                return HLSLTranslationResult.Failure($"No [Kernel] methods found in struct '{shaderType.Name}'", shaderType.Name);

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

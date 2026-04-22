using DivisionEngine.MathLib;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;

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
                MetadataReference.CreateFromFile(typeof(Graphics.Buffer<>).Assembly.Location),
            ];
        }

        public CompiledDivisionShader CompileShader(Type shaderType)
        {
            // Get the source file path
            string sourcePath = GetSourceFilePath(shaderType);
            if (!File.Exists(sourcePath))
                return CompiledDivisionShader.Failure($"Source file not found: {sourcePath}");

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
                return CompiledDivisionShader.Failure($"Struct '{shaderType.Name}' not found in source file");

            // Find kernel method within specific struct declaration
            MethodDeclarationSyntax? kernelMethod = FindKernelMethodInStruct(structDeclaration, semanticModel);
            if (kernelMethod == null)
                return CompiledDivisionShader.Failure($"No [Kernel] method found in struct '{shaderType.Name}'");

            // Translate using C# Roslyn semantic walker
            DivisionTranslator translator = new DivisionTranslator(semanticModel);
            string hlslCode = translator.Translate(structDeclaration);
            return CompiledDivisionShader.Success(hlslCode);
        }

        private static StructDeclarationSyntax? FindStructDeclaration(SyntaxNode root, string structName) => root.DescendantNodes()
                .OfType<StructDeclarationSyntax>()
                .FirstOrDefault(structDecl => structDecl.Identifier.Text == structName);

        private static MethodDeclarationSyntax? FindKernelMethodInStruct(StructDeclarationSyntax structDeclaration, SemanticModel semanticModel)
        {
            var methods = structDeclaration.DescendantNodes().OfType<MethodDeclarationSyntax>();
            foreach (MethodDeclarationSyntax method in methods)
            {
                IMethodSymbol? methodSymbol = semanticModel.GetDeclaredSymbol(method);
                if (methodSymbol?.GetAttributes().Any(attr => attr.AttributeClass?.Name == "KernelAttribute") == true)
                    return method;
            }
            return null;
        }

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

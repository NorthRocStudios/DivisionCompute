//
// Copyright (c) 2026 Rex Woodfield and DivisionCompute contributors
//
// This file is part of DivisionCompute and is subject to the terms
// of the DivisionCompute License. See the LICENSE.txt file in the
// project root for full license terms.
//
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace DivisionCompute.SourceGenerators
{
    /// <summary>
    /// Translates every <c>[Shader]</c> struct in the compilation to HLSL at build
    /// time and bakes the result - HLSL text, kernel names, resource layout, and
    /// packed uniform layout - into the assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The generated code registers a <see cref="DivisionEngine.DivisionShaderDescriptor"/>
    /// from a module initializer, so <see cref="DivisionEngine.DivisionShaderDescriptors.TryGet"/>
    /// finds the shader before any user code runs. The runtime then skips Roslyn and
    /// reflection entirely for these shaders.
    /// </para>
    /// <para>
    /// Runtime translation is untouched: a type with no generated descriptor (or one
    /// marked <c>[Shader(GenerateDescriptor = false)]</c>) still goes through the
    /// Roslyn translator, which is what hot reload needs.
    /// </para>
    /// <para>
    /// Translation failures become build errors, so an unsupported construct is
    /// reported in the editor instead of throwing when the shader is first compiled.
    /// </para>
    /// </remarks>
    [Generator(LanguageNames.CSharp)]
    public sealed class DivisionShaderGenerator : IIncrementalGenerator
    {
        private const string ShaderAttributeMetadataName = "DivisionEngine.ShaderAttribute";
        private const string GenerateDescriptorPropertyName = "GenerateDescriptor";

        private static readonly DiagnosticDescriptor TranslationFailed = new(
            id: "DIVSGEN001",
            title: "Shader could not be translated",
            messageFormat: "Shader '{0}' could not be translated to HLSL: {1}",
            category: "DivisionCompute.Shaders",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "Fix the shader or move the unsupported construct out of the kernel. " +
                         "Use [Shader(GenerateDescriptor = false)] to translate this shader at runtime instead.");

        private static readonly DiagnosticDescriptor NoKernelsFound = new(
            id: "DIVSGEN002",
            title: "Shader has no kernels",
            messageFormat: "Shader '{0}' has no methods marked with [Kernel]",
            category: "DivisionCompute.Shaders",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "A [Shader] struct needs at least one [Kernel] method to be dispatchable.");

        /// <inheritdoc/>
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            IncrementalValuesProvider<ShaderModel?> models =
                context.SyntaxProvider.ForAttributeWithMetadataName(
                    ShaderAttributeMetadataName,
                    static (node, _) => node is StructDeclarationSyntax,
                    static (attributeContext, token) => CreateModel(attributeContext, token));

            context.RegisterSourceOutput(models, static (productionContext, model) =>
            {
                if (model is not null)
                    Emit(productionContext, model);
            });
        }

        private static ShaderModel? CreateModel(GeneratorAttributeSyntaxContext context, CancellationToken token)
        {
            if (context.TargetSymbol is not INamedTypeSymbol shaderSymbol ||
                context.TargetNode is not StructDeclarationSyntax structDeclaration)
            {
                return null;
            }

            // Opt-out for shaders that must be translated (or must fail) at runtime.
            if (IsDescriptorGenerationDisabled(context.Attributes))
                return null;

            var location = SourceLocation.From(structDeclaration.Identifier.GetLocation());

            if (shaderSymbol.IsGenericType)
                return ShaderModel.Failure(shaderSymbol, location, "Shader structs cannot be generic.");

            if (context.SemanticModel.Compilation is not CSharpCompilation compilation)
                return ShaderModel.Failure(shaderSymbol, location, "The containing compilation is not a C# compilation.");

            token.ThrowIfCancellationRequested();

            // Record the source file and its build-time hash so the runtime can tell
            // whether the shader was edited after this build (hot reload). File access
            // is deliberate here and RS1035 is suppressed for this project.
            string sourceFilePath = structDeclaration.SyntaxTree.FilePath ?? string.Empty;
            string? sourceHash = null;

            if (!string.IsNullOrEmpty(sourceFilePath) && File.Exists(sourceFilePath))
                sourceHash = ComputeSourceHash(sourceFilePath);

            try
            {
                var translator = new DivisionEngine.DivisionTranslator(
                    context.SemanticModel,
                    shaderType: null,
                    compilation);

                string hlsl = translator.Translate(structDeclaration);

                if (translator.KernelNames.Count == 0)
                    return ShaderModel.NoKernelsFound(shaderSymbol, location);

                List<DivisionEngine.SymbolResourceField> resources =
                    DivisionEngine.ShaderSymbolLayout.GetResources(shaderSymbol);

                List<DivisionEngine.SymbolUniformField> uniforms =
                    DivisionEngine.ShaderSymbolLayout.GetUniforms(shaderSymbol);

                int uniformByteSize = DivisionEngine.ShaderSymbolLayout.GetUniformByteSize(uniforms);

                return ShaderModel.Success(
                    shaderSymbol,
                    hlsl,
                    translator.KernelNames,
                    resources,
                    uniforms,
                    uniformByteSize,
                    location,
                    string.IsNullOrEmpty(sourceFilePath) ? null : sourceFilePath,
                    sourceHash);
            }
            catch (Exception ex) when (
                ex is NotSupportedException or InvalidOperationException or ArgumentException or FormatException)
            {
                return ShaderModel.Failure(shaderSymbol, location, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A translator bug must not break the build with an unhandled
                // exception; report it as a diagnostic instead.
                return ShaderModel.Failure(shaderSymbol, location, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool IsDescriptorGenerationDisabled(IEnumerable<AttributeData> attributes)
        {
            foreach (AttributeData attribute in attributes)
            {
                foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
                {
                    if (argument.Key == GenerateDescriptorPropertyName &&
                        argument.Value.Value is false)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static void Emit(SourceProductionContext context, ShaderModel model)
        {
            if (model.Error is not null)
            {
                DiagnosticDescriptor descriptor = model.NoKernels ? NoKernelsFound : TranslationFailed;

                context.ReportDiagnostic(Diagnostic.Create(
                    descriptor,
                    model.Location?.ToLocation(),
                    model.ShaderName,
                    model.Error));

                return;
            }

            context.AddSource(
                $"{model.HintName}.DivisionShader.g.cs",
                SourceText.From(GenerateSource(model), Encoding.UTF8));
        }

        private static string GenerateSource(ShaderModel model)
        {
            var source = new StringBuilder();
            source.AppendLine("// <auto-generated/>");
            source.AppendLine("#nullable enable");
            source.AppendLine("#pragma warning disable CS1591");
            source.AppendLine();
            source.AppendLine("namespace DivisionEngine.Generated");
            source.AppendLine("{");
            source.AppendLine($"    internal static class DivisionShaderRegistration_{Sanitize(model.HintName)}");
            source.AppendLine("    {");
            source.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
            source.AppendLine("        internal static void Register()");
            source.AppendLine("        {");
            source.AppendLine("            global::DivisionEngine.DivisionShaderDescriptors.Register(");
            source.AppendLine($"                typeof({model.ShaderTypeDisplay}),");
            source.AppendLine("                new global::DivisionEngine.DivisionShaderDescriptor(");
            source.AppendLine($"                    {StringLiteral(model.ShaderName)},");
            source.AppendLine($"                    {VerbatimLiteral(model.HlslCode)},");
            source.AppendLine($"                    {KernelArray(model.KernelNames)},");
            source.AppendLine($"                    {ResourceArray(model.Resources)},");
            source.AppendLine($"                    {UniformArray(model.Uniforms)},");
            source.AppendLine($"                    {model.UniformByteSize},");
            source.AppendLine($"                    {StringLiteralOrNull(model.SourceFilePath)},");
            source.AppendLine($"                    {StringLiteralOrNull(model.SourceHash)}));");
            source.AppendLine("        }");
            source.AppendLine("    }");
            source.AppendLine("}");
            return source.ToString();
        }

        private static string KernelArray(EquatableArray<string> kernelNames)
        {
            if (kernelNames.Count == 0)
                return "global::System.Array.Empty<string>()";

            var builder = new StringBuilder("new string[] { ");
            for (int i = 0; i < kernelNames.Count; i++)
            {
                if (i > 0) builder.Append(", ");
                builder.Append(StringLiteral(kernelNames[i]));
            }

            return builder.Append(" }").ToString();
        }

        private static string ResourceArray(EquatableArray<ResourceModel> resources)
        {
            if (resources.Count == 0)
                return "global::System.Array.Empty<global::DivisionEngine.DivisionResourceBinding>()";

            var builder = new StringBuilder("new global::DivisionEngine.DivisionResourceBinding[] { ");
            for (int i = 0; i < resources.Count; i++)
            {
                if (i > 0) builder.Append(", ");

                ResourceModel resource = resources[i];
                string kind = resource.IsUav
                    ? "global::DivisionEngine.DivisionResourceKind.Uav"
                    : "global::DivisionEngine.DivisionResourceKind.Srv";

                builder.Append(
                    $"new global::DivisionEngine.DivisionResourceBinding({StringLiteral(resource.Name)}, {resource.Slot}u, {kind})");
            }

            return builder.Append(" }").ToString();
        }

        private static string UniformArray(EquatableArray<UniformModel> uniforms)
        {
            if (uniforms.Count == 0)
                return "global::System.Array.Empty<global::DivisionEngine.DivisionUniformBinding>()";

            var builder = new StringBuilder("new global::DivisionEngine.DivisionUniformBinding[] { ");
            for (int i = 0; i < uniforms.Count; i++)
            {
                if (i > 0) builder.Append(", ");

                UniformModel uniform = uniforms[i];
                builder.Append(
                    $"new global::DivisionEngine.DivisionUniformBinding({StringLiteral(uniform.Name)}, (global::DivisionEngine.UniformKind){uniform.Kind}, {uniform.Size}, {uniform.Offset})");
            }

            return builder.Append(" }").ToString();
        }

        private static string StringLiteral(string value)
            => SymbolDisplay.FormatLiteral(value, quote: true);

        /// <summary>
        /// Hashes the shader source in a platform-independent form: line endings are
        /// normalized and the BOM is dropped, so a checkout that only changes CRLF/LF
        /// is not mistaken for an edit. The runtime uses the same normalization.
        /// </summary>
        private static string? ComputeSourceHash(string path)
        {
            try
            {
                string text = File.ReadAllText(path)
                    .Replace("\r\n", "\n")
                    .Replace('\r', '\n');

                return Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Best effort: without a hash the descriptor is always trusted.
                return null;
            }
        }

        private static string StringLiteralOrNull(string? value)
            => value is null ? "null" : SymbolDisplay.FormatLiteral(value, quote: true);

        private static string VerbatimLiteral(string value)
            => "@\"" + value.Replace("\"", "\"\"") + "\"";

        private static string Sanitize(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (char c in value)
                builder.Append(char.IsLetterOrDigit(c) ? c : '_');

            return builder.ToString();
        }
    }
}

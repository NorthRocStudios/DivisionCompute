//
// Copyright (c) 2026 Rex Woodfield and DivisionCompute contributors
//
// This file is part of DivisionCompute and is subject to the terms
// of the DivisionCompute License. See the LICENSE.txt file in the
// project root for full license terms.
//
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace DivisionCompute.SourceGenerators
{
    /// <summary>
    /// A source location captured in an equatable form, so the incremental pipeline
    /// can cache the model.
    /// </summary>
    internal readonly record struct SourceLocation(
        string FilePath,
        int Start,
        int Length,
        int StartLine,
        int StartCharacter,
        int EndLine,
        int EndCharacter)
    {
        public static SourceLocation From(Location location)
        {
            FileLinePositionSpan span = location.GetLineSpan();
            LinePosition start = span.StartLinePosition;
            LinePosition end = span.EndLinePosition;

            return new SourceLocation(
                span.Path,
                location.SourceSpan.Start,
                location.SourceSpan.Length,
                start.Line,
                start.Character,
                end.Line,
                end.Character);
        }

        public Location ToLocation() => Location.Create(
            FilePath,
            new TextSpan(Start, Length),
            new LinePositionSpan(
                new LinePosition(StartLine, StartCharacter),
                new LinePosition(EndLine, EndCharacter)));
    }

    /// <summary>
    /// One resource binding captured for code generation.
    /// </summary>
    internal readonly record struct ResourceModel(string Name, uint Slot, bool IsUav);

    /// <summary>
    /// One packed uniform captured for code generation.
    /// </summary>
    internal readonly record struct UniformModel(string Name, int Kind, int Size, int Offset);

    /// <summary>
    /// Everything the generator needs to emit a shader descriptor, in an equatable
    /// form so an unchanged shader produces no new source.
    /// </summary>
    internal sealed record ShaderModel(
        string HintName,
        string ShaderTypeDisplay,
        string ShaderName,
        string HlslCode,
        EquatableArray<string> KernelNames,
        EquatableArray<ResourceModel> Resources,
        EquatableArray<UniformModel> Uniforms,
        int UniformByteSize,
        SourceLocation? Location,
        string? Error,
        bool NoKernels,
        string? SourceFilePath,
        string? SourceHash)
    {
        public static ShaderModel Success(
            INamedTypeSymbol shaderSymbol,
            string hlsl,
            IReadOnlyList<string> kernelNames,
            IReadOnlyList<DivisionEngine.SymbolResourceField> resources,
            IReadOnlyList<DivisionEngine.SymbolUniformField> uniforms,
            int uniformByteSize,
            SourceLocation location,
            string? sourceFilePath,
            string? sourceHash)
        {
            var resourceModels = new ResourceModel[resources.Count];
            for (int i = 0; i < resources.Count; i++)
            {
                DivisionEngine.SymbolResourceField resource = resources[i];
                resourceModels[i] = new ResourceModel(
                    resource.Name,
                    resource.Slot,
                    resource.Kind == DivisionEngine.DivisionResourceKind.Uav);
            }

            var uniformModels = new UniformModel[uniforms.Count];
            for (int i = 0; i < uniforms.Count; i++)
            {
                DivisionEngine.SymbolUniformField uniform = uniforms[i];
                uniformModels[i] = new UniformModel(
                    uniform.Name,
                    (int)uniform.Kind,
                    uniform.Size,
                    uniform.Offset);
            }

            return new ShaderModel(
                shaderSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty),
                shaderSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                shaderSymbol.Name,
                hlsl,
                new EquatableArray<string>([.. kernelNames]),
                new EquatableArray<ResourceModel>(resourceModels),
                new EquatableArray<UniformModel>(uniformModels),
                uniformByteSize,
                location,
                Error: null,
                NoKernels: false,
                SourceFilePath: sourceFilePath,
                SourceHash: sourceHash);
        }

        public static ShaderModel Failure(INamedTypeSymbol shaderSymbol, SourceLocation location, string error)
            => Empty(shaderSymbol, location, error, noKernels: false);

        public static ShaderModel NoKernelsFound(INamedTypeSymbol shaderSymbol, SourceLocation location)
            => Empty(shaderSymbol, location, "No [Kernel] methods were found.", noKernels: true);

        private static ShaderModel Empty(INamedTypeSymbol shaderSymbol, SourceLocation location, string error, bool noKernels)
            => new(
                shaderSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty),
                shaderSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                shaderSymbol.Name,
                HlslCode: string.Empty,
                KernelNames: EquatableArray<string>.Empty,
                Resources: EquatableArray<ResourceModel>.Empty,
                Uniforms: EquatableArray<UniformModel>.Empty,
                UniformByteSize: 0,
                Location: location,
                Error: error,
                NoKernels: noKernels,
                SourceFilePath: null,
                SourceHash: null);
    }
}

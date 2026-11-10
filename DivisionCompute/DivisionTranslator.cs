//
// Copyright (c) 2026 Rex Woodfield and DivisionCompute contributors
//
// This file is part of DivisionCompute and is subject to the terms
// of the DivisionCompute License. See the LICENSE.txt file in the
// project root for full license terms.
//
using DivisionEngine.Graphics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;
using System.Text;
using TypeInfo = Microsoft.CodeAnalysis.TypeInfo;

namespace DivisionEngine
{
    /// <summary>
    /// Syntax walker that translates C# source code to HLSL source code based on the ruleset defined for the Division Engine.
    /// </summary>
    public class DivisionTranslator : CSharpSyntaxWalker
    {
        // Translation vars
        private SemanticModel semanticModel;
        private readonly Type? shaderType;
        private readonly CSharpCompilation? compilation;
        private readonly Dictionary<string, FieldInfo> shaderFields;
        private readonly Dictionary<string, Type> knownTypes;
        private readonly StringBuilder hlsl = new StringBuilder();
        private int indentLvl = 0;
        private int? threadsX, threadsY, threadsZ;

        // Storage for translation
        private readonly List<string> resourceDeclarations = [];
        private readonly List<string> variableDeclarations = [];
        private readonly List<string> structDeclarations = [];
        private readonly List<string> inlineFunctions = [];
        private readonly List<string> inlineFunctionSignatures = [];
        private readonly List<string> constantDeclarations = [];
        private readonly List<string> kernelNames = [];
        private readonly HashSet<string> processedStructs = [];
        private readonly Dictionary<string, string> bufferLengthHelpers = [];

        // External static helper classes (ShaderMath, PBR, ...) translated on demand.
        private readonly Dictionary<string, string> helperMethodNames = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> helperMethodBodies = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> helperConstantNames = new(StringComparer.Ordinal);
        private readonly HashSet<string> helperMethodsInProgress = new(StringComparer.Ordinal);
        private readonly HashSet<string> emittedNames = new(StringComparer.Ordinal);
        private INamedTypeSymbol? currentHelperType;

        /// <summary>
        /// Creates a translator for one shader struct.
        /// </summary>
        /// <param name="semanticModel">Semantic model of the shader's source file.</param>
        /// <param name="shaderType">Runtime type of the shader struct, when known.</param>
        /// <param name="compilation">
        /// Compilation that includes the shader's source and the source of any helper
        /// classes it calls. Required to translate external static helper methods.
        /// </param>
        public DivisionTranslator(SemanticModel semanticModel, Type? shaderType = null, CSharpCompilation? compilation = null)
        {
            this.semanticModel = semanticModel;
            this.shaderType = shaderType;
            this.compilation = compilation;
            shaderFields = BuildShaderFields(shaderType);
            knownTypes = BuildKnownTypes(shaderType);
        }

        /// <summary>
        /// Translates C# source code to HLSL source code.
        /// </summary>
        /// <param name="shaderStruct">The struct containing the shader</param>
        /// <returns>HLSL source code</returns>
        public string Translate(StructDeclarationSyntax shaderStruct)
        {
            List<MethodDeclarationSyntax> kernels = FindAllKernels(shaderStruct);
            List<MethodDeclarationSyntax> inlineFuncs = FindAllInlineFunctions(shaderStruct);
            CollectFields(shaderStruct);

            foreach (MethodDeclarationSyntax func in inlineFuncs) TranslateInlineFunction(func);

            // Translate kernel bodies before emitting anything. Their bodies can
            // discover structured-buffer Length/Count uses, which must be emitted
            // as helper definitions before the kernels that call them.
            var kernelDefinitions = new List<string>(kernels.Count);
            foreach (MethodDeclarationSyntax kernel in kernels)
                kernelDefinitions.Add(TranslateKernelToString(kernel));

            hlsl.AppendLine();

            // Structs must be declared before any resource, function, or global that
            // mentions them. Reflection-emitted structs are added dependencies-first.
            foreach (string structDecl in structDeclarations) hlsl.AppendLine(structDecl);

            // Constants (shader struct consts and helper-class consts) come next so
            // the uniform buffer and every function can reference them.
            foreach (string constant in constantDeclarations) hlsl.AppendLine(constant);
            if (constantDeclarations.Count > 0) hlsl.AppendLine();

            // Shader uniforms (plain fields) live in one constant buffer at b0, the
            // same shape Unity's compute shaders use: declare fields, set them by
            // name from C#, read them as plain identifiers in HLSL.
            List<string> uniformDeclarations = GetUniformDeclarations();
            if (uniformDeclarations.Count > 0)
            {
                hlsl.AppendLine($"cbuffer {DivisionShader.UniformBufferName} : register(b0)");
                hlsl.AppendLine("{");
                foreach (string declaration in uniformDeclarations) hlsl.AppendLine($"    {declaration}");
                hlsl.AppendLine("};");
                hlsl.AppendLine();
            }

            // Resources and globals must be declared before inline functions that
            // reference them; HLSL requires declarations before use.
            foreach (string resource in resourceDeclarations) hlsl.AppendLine(resource);

            // Emit a default sampler whenever any read-only Texture2D exists.
            // The HLSL Sample/SampleLevel intrinsics require one; we wire it to s0
            // so it doesn't collide with the t/u/b register slots
            if (UsesReadOnlyTexture2D())
                hlsl.AppendLine("SamplerState DivisionDefaultSampler : register(s0);");

            foreach (string helper in bufferLengthHelpers.Values) hlsl.AppendLine(helper);

            // HLSL requires a declaration before use. Emit prototypes so helper
            // methods can call each other regardless of the order they appear in the
            // C# struct (C# has no such ordering rule).
            foreach (string signature in inlineFunctionSignatures) hlsl.AppendLine($"{signature};");
            if (inlineFunctionSignatures.Count > 0) hlsl.AppendLine();

            foreach (string func in inlineFunctions) hlsl.AppendLine(func);

            // Helper-class functions are discovered while translating bodies; the
            // prototypes above make their order irrelevant.
            foreach (string helper in helperMethodBodies.Values) hlsl.AppendLine(helper);

            if (kernelDefinitions.Count > 0) hlsl.AppendLine();

            for (int i = 0; i < kernelDefinitions.Count; i++)
            {
                if (i > 0) hlsl.AppendLine();
                hlsl.Append(kernelDefinitions[i]);
            }
            return hlsl.ToString();
        }

        /// <summary>
        /// Statements that declare out-arguments get those locals emitted first.
        /// </summary>
        public override void Visit(SyntaxNode? node)
        {
            if (node is StatementSyntax and not BlockSyntax)
                HoistOutArguments(node);

            base.Visit(node);
        }

        /// <summary>
        /// Translates a kernel into a standalone string while preserving the main
        /// output buffer.
        /// </summary>
        private string TranslateKernelToString(MethodDeclarationSyntax kernelMethod)
        {
            string original = hlsl.ToString();
            int oldIndent = indentLvl;

            hlsl.Clear();
            indentLvl = 0;
            TranslateKernel(kernelMethod);
            string result = hlsl.ToString();

            hlsl.Clear();
            hlsl.Append(original);
            indentLvl = oldIndent;

            return result;
        }

        /// <summary>
        /// True if any [ShaderResource] field is a read-only Texture2D&lt;T&gt;
        /// (as opposed to RWTexture2D&lt;T&gt;, which doesn't sample).
        /// </summary>
        private bool UsesReadOnlyTexture2D()
        {
            foreach (string decl in resourceDeclarations)
                if (decl.TrimStart().StartsWith("Texture2D<"))
                    return true;
            return false;
        }

        /// <summary>
        /// Discovers all kernels in the shader struct declaration syntax.
        /// </summary>
        /// <returns>List of all method declaration kernels in C#</returns>
        private List<MethodDeclarationSyntax> FindAllKernels(StructDeclarationSyntax shaderStruct)
        {
            List<MethodDeclarationSyntax> kernels = [];
            foreach (MethodDeclarationSyntax method in shaderStruct.Members.OfType<MethodDeclarationSyntax>())
            {
                bool hasKernelAttr = method.AttributeLists
                    .SelectMany(list => list.Attributes)
                    .Any(attr => attr.Name.ToString() == "Kernel");
                if (hasKernelAttr)
                {
                    kernels.Add(method);
                    kernelNames.Add(method.Identifier.Text);
                }
            }
            return kernels;
        }

        /// <summary>
        /// Translates a single kernel based on C# equivalent method.
        /// </summary>
        /// <summary>
        /// Hoists C# 7 out-argument declarations ("out int offset", "out int2 res")
        /// and out-discards ("out _") above the statement that uses them. HLSL cannot
        /// declare a variable inside an argument list, so "GetMipInfo(m, lvl, out int
        /// offset)" has to become "int offset; GetMipInfo(m, lvl, offset);".
        /// </summary>
        private void HoistOutArguments(SyntaxNode statement)
        {
            outArgumentLocals.Clear();

            foreach (ArgumentSyntax argument in statement.DescendantNodes().OfType<ArgumentSyntax>())
            {
                if (!argument.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword)) continue;

                // Only this statement's own expressions: nested statements hoist their
                // own out-arguments when they are visited.
                if (argument.Ancestors().OfType<StatementSyntax>().FirstOrDefault() != statement) continue;

                string? typeName;
                string name;

                if (argument.Expression is DeclarationExpressionSyntax declaration &&
                    declaration.Designation is SingleVariableDesignationSyntax designation)
                {
                    typeName = ConvertType(semanticModel.GetTypeInfo(declaration.Type).Type);
                    name = designation.Identifier.Text;
                }
                else if (argument.Expression is IdentifierNameSyntax identifier && identifier.Identifier.Text == "_")
                {
                    // "out _" has no variable to name; the callee still requires one.
                    InvocationExpressionSyntax? invocation = argument.FirstAncestorOrSelf<InvocationExpressionSyntax>();
                    if (invocation is null) continue;
                    IMethodSymbol? method = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                    int index = invocation.ArgumentList.Arguments.IndexOf(argument);
                    if (method is null || index < 0 || index >= method.Parameters.Length) continue;
                    typeName = ConvertType(method.Parameters[index].Type);
                    name = $"divisionDiscard{outDiscardCounter++}";
                }
                else continue;

                if (string.IsNullOrEmpty(typeName) || typeName == "void") continue;

                outArgumentLocals[argument] = name;
                WriteIndent();
                hlsl.AppendLine($"{typeName} {name};");
            }
        }

        /// <summary>
        /// Emits an invocation argument, using the hoisted local for out-declarations.
        /// </summary>
        private void AppendArgument(ArgumentSyntax argument)
        {
            if (outArgumentLocals.TryGetValue(argument, out string? hoisted))
            {
                hlsl.Append(hoisted);
                return;
            }

            Visit(argument.Expression);
        }

        private void TranslateKernel(MethodDeclarationSyntax kernelMethod)
        {
            GetThreadCounts(kernelMethod); // Get thread counts from Kernel attribute
            hlsl.AppendLine($"[numthreads({threadsX ?? 64}, {threadsY ?? 1}, {threadsZ ?? 1})]");
            hlsl.AppendLine($"void {kernelMethod.Identifier.Text}(uint3 id : SV_DispatchThreadID)");
            hlsl.AppendLine("{");
            indentLvl++;
            foreach (StatementSyntax statement in kernelMethod.Body!.Statements) Visit(statement);
            indentLvl--;
            hlsl.AppendLine("}");
        }

        #region StatementVisitors

        /// <summary>
        /// Translates "default(T)" into a zero-initialized value.
        /// </summary>
        public override void VisitDefaultExpression(DefaultExpressionSyntax node)
            => AppendDefaultValue(semanticModel.GetTypeInfo(node.Type).Type);

        /// <summary>
        /// Emits a zero value for a type: a broadcast vector constructor, "(T)0" for a
        /// struct, or a plain 0 for scalars.
        /// </summary>
        private void AppendDefaultValue(ITypeSymbol? type)
        {
            // A default literal inside a larger expression has no type of its own; the
            // converted type (the target of the assignment) is the useful one.
            string typeName = ConvertType(type);
            if (type is null || type.SpecialType != SpecialType.None)
            {
                hlsl.Append(type?.SpecialType == SpecialType.System_Boolean ? "false" : "0");
                return;
            }

            if (GetVectorComponentCount(type) > 0)
            {
                hlsl.Append($"{typeName}(0.0)");
                return;
            }

            hlsl.Append($"({typeName})0");
        }

        /// <summary>
        /// Visits statements like "float3 pos = new float3(1,2,3);".
        /// </summary>
        public override void VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            TypeInfo typeInfo = semanticModel.GetTypeInfo(node.Declaration.Type);
            string hlslType = ConvertType(typeInfo.Type);

            var variables = node.Declaration.Variables;
            foreach (VariableDeclaratorSyntax variable in variables) // Works for nested initialization, eg. int a = 1, b = 3, c = 3;
            {
                ShaderStructLayout.ValidateHlslIdentifier(
                    variable.Identifier.Text, "local variable '" + variable.Identifier.Text + "'");
                WriteIndent();
                hlsl.Append($"{hlslType} {variable.Identifier.Text}");
                if (variable.Initializer != null)
                {
                    hlsl.Append(" = ");
                    Visit(variable.Initializer.Value);
                }
                hlsl.AppendLine(";");
            }
        }

        /// <summary>
        /// Translates expression statements.
        /// </summary>
        public override void VisitExpressionStatement(ExpressionStatementSyntax node)
        {
            WriteIndent();
            Visit(node.Expression);
            hlsl.AppendLine(";");
        }

        /// <summary>
        /// Translates a for loop.
        /// </summary>
        public override void VisitForStatement(ForStatementSyntax node)
        {
            // "[loop]" stops the optimizer from unrolling. Shader loops here are either
            // data-dependent raymarch loops or nested noise loops; letting fxc unroll
            // them made compilation of the SDF shader take over ten minutes.
            WriteIndent();
            hlsl.AppendLine("[loop]");
            WriteIndent();

            hlsl.Append("for ("); // Open loop description
            if (node.Declaration != null) // Initializers (int i = 0)
            {
                TypeInfo typeInfo = semanticModel.GetTypeInfo(node.Declaration.Type);
                string hlslType = ConvertType(typeInfo.Type);
                hlsl.Append($"{hlslType} {node.Declaration.Variables.First()}");
            }
            else if (node.Initializers.Count > 0)
            {
                for (int i = 0; i < node.Initializers.Count; i++)
                {
                    if (i > 0) hlsl.Append(", ");
                    Visit(node.Initializers[i]);
                }
            }

            hlsl.Append("; ");
            if (node.Condition != null) Visit(node.Condition); // Condition (i < 5)
            hlsl.Append("; ");

            for (int i = 0; i < node.Incrementors.Count; i++) // Incrementors (i++)
            {
                if (i > 0) hlsl.Append(", ");
                Visit(node.Incrementors[i]);
            }
            hlsl.AppendLine(")"); // Close loop description

            // Handle for loop body, always use braces
            if (node.Statement is BlockSyntax block)
                Visit(block);
            else
            {
                indentLvl++;
                Visit(node.Statement);
                indentLvl--;
            }
        }

        /// <summary>
        /// Translates an if statement chain.
        /// </summary>
        public override void VisitIfStatement(IfStatementSyntax node)
        {
            WriteIndent();
            hlsl.Append("if (");
            Visit(node.Condition);
            hlsl.AppendLine(")");

            if (node.Statement is BlockSyntax block) // Handle then statement
                Visit(block);
            else
            {
                indentLvl++;
                Visit(node.Statement);
                indentLvl--;
            }

            if (node.Else != null) // Handle else statement
            {
                WriteIndent();
                hlsl.AppendLine("else");

                if (node.Else.Statement is BlockSyntax elseBlock)
                    Visit(elseBlock);
                else
                {
                    indentLvl++;
                    Visit(node.Else.Statement);
                    indentLvl--;
                }
            }
        }

        /// <summary>
        /// Translates do-while loops.
        /// </summary>
        public override void VisitDoStatement(DoStatementSyntax node)
        {
            WriteIndent();
            hlsl.AppendLine("[loop]");
            WriteIndent();
            hlsl.AppendLine("do");

            if (node.Statement is BlockSyntax block)
                Visit(block);
            else
            {
                indentLvl++;
                Visit(node.Statement);
                indentLvl--;
            }

            WriteIndent();
            hlsl.Append("while (");
            Visit(node.Condition);
            hlsl.AppendLine(");");
        }

        /// <summary>
        /// Translates while loops.
        /// </summary>
        public override void VisitWhileStatement(WhileStatementSyntax node)
        {
            WriteIndent();
            hlsl.AppendLine("[loop]");
            WriteIndent();
            hlsl.Append("while (");
            Visit(node.Condition);
            hlsl.AppendLine(")");

            if (node.Statement is BlockSyntax block)
                Visit(block);
            else
            {
                indentLvl++;
                Visit(node.Statement);
                indentLvl--;
            }
        }

        /// <summary>
        /// Translates switch statements. HLSL supports C# style switch/case/default.
        /// </summary>
        public override void VisitSwitchStatement(SwitchStatementSyntax node)
        {
            WriteIndent();
            hlsl.Append("switch (");
            Visit(node.Expression);
            hlsl.AppendLine(")");
            WriteIndent();
            hlsl.AppendLine("{");
            indentLvl++;

            foreach (SwitchSectionSyntax section in node.Sections)
            {
                foreach (SwitchLabelSyntax label in section.Labels)
                {
                    WriteIndent();
                    switch (label)
                    {
                        case CaseSwitchLabelSyntax caseLabel:
                            hlsl.Append("case ");
                            Visit(caseLabel.Value);
                            hlsl.AppendLine(":");
                            break;
                        case DefaultSwitchLabelSyntax:
                            hlsl.AppendLine("default:");
                            break;
                        default:
                            throw new NotSupportedException(
                                $"Switch label '{label}' is not supported in shader code. " +
                                "Use plain case/default labels with constant values.");
                    }
                }

                indentLvl++;
                foreach (StatementSyntax statement in section.Statements) Visit(statement);
                indentLvl--;
            }

            indentLvl--;
            WriteIndent();
            hlsl.AppendLine("}");
        }

        /// <summary>
        /// Translates break; statements.
        /// </summary>
        public override void VisitBreakStatement(BreakStatementSyntax node)
        {
            WriteIndent();
            hlsl.AppendLine("break;");
        }

        /// <summary>
        /// Translates continue; statements.
        /// </summary>
        public override void VisitContinueStatement(ContinueStatementSyntax node)
        {
            WriteIndent();
            hlsl.AppendLine("continue;");
        }

        /// <summary>
        /// Translates return statements.
        /// </summary>
        public override void VisitReturnStatement(ReturnStatementSyntax node)
        {
            WriteIndent();
            hlsl.Append("return");
            if (node.Expression != null)
            {
                hlsl.Append(' ');
                Visit(node.Expression);
            }
            hlsl.AppendLine(";");
        }

        /// <summary>
        /// Shouldn't be called, fallback statement translation.
        /// </summary>
        public override void VisitEmptyStatement(EmptyStatementSyntax node) { }

        #endregion StatementVisitors
        #region ExpressionVisitors

        /// <summary>
        /// Translates object creation expressions like "new float3(1,2,3)".
        /// </summary>
        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            TypeInfo typeInfo = semanticModel.GetTypeInfo(node.Type); // Get the type being created
            string typeName = ConvertType(typeInfo.Type);
            var arguments = node.ArgumentList!.Arguments;

            // HLSL numeric constructors need one argument per component (or a set of
            // vectors that add up to it). The scalar broadcast form float4(x) that
            // DivisionMath supports does not compile under D3DCompile, so emit the
            // cast form instead - it broadcasts and evaluates x exactly once.
            if (arguments.Count == 1 &&
                GetVectorComponentCount(typeInfo.Type) > 1 &&
                IsScalarType(semanticModel.GetTypeInfo(arguments[0].Expression).Type))
            {
                hlsl.Append($"({typeName})(");
                Visit(arguments[0].Expression);
                hlsl.Append(')');
                return;
            }

            // "new T()" for a struct is a zero-initialized value. HLSL has no default
            // constructor for structs; "(T)0" is its zero-init idiom.
            if (arguments.Count == 0 &&
                typeInfo.Type is { TypeKind: TypeKind.Struct } structType &&
                GetVectorComponentCount(structType) == 0)
            {
                hlsl.Append($"({typeName})0");
                return;
            }

            hlsl.Append($"{typeName}("); // Open argument list
            for (int i = 0; i < arguments.Count; i++) // Visit each argument
            {
                if (i > 0) hlsl.Append(", ");
                Visit(arguments[i].Expression);
            }
            hlsl.Append(')'); // Close argument list
        }

        /// <summary>
        /// Returns the number of components of an HLSL vector type, or 0 when the
        /// type is not a vector.
        /// </summary>
        private static int GetVectorComponentCount(ITypeSymbol? type)
        {
            if (type is null) return 0;

            if (type.ContainingNamespace?.ToDisplayString() == "System.Numerics" &&
                type.Name is "Vector2" or "Vector3" or "Vector4")
            {
                return type.Name[^1] - '0';
            }

            string name = type.Name;
            if (name.Length >= 2 && name[^1] is '2' or '3' or '4')
            {
                string prefix = name[..^1];
                if (prefix is "float" or "int" or "uint" or "double" or "bool" or "half")
                    return name[^1] - '0';
            }

            return 0;
        }

        /// <summary>
        /// True when the type is a scalar that HLSL can broadcast into a vector.
        /// </summary>
        private static bool IsScalarType(ITypeSymbol? type)
        {
            if (type is null) return false;
            if (type.TypeKind == TypeKind.Enum) return true;

            return type.SpecialType is
                SpecialType.System_Single or
                SpecialType.System_Double or
                SpecialType.System_Int32 or
                SpecialType.System_UInt32 or
                SpecialType.System_Boolean;
        }

        /// <summary>
        /// Translates invocation expressions like "math.length(pos)" or "input.Sample(uv)".
        /// </summary>
        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            IMethodSymbol? methodSymbol = semanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol;

            // Texture2D extension methods 
            // C#: input.Sample(uv) → HLSL: input.Sample(DivisionDefaultSampler, uv)
            // C#: input.Load(coord) → HLSL: input.Load(int3(coord, 0))
            if (methodSymbol is { MethodKind: MethodKind.ReducedExtension } &&
                methodSymbol.ContainingType?.Name == "Texture2DExtensions" &&
                node.Expression is MemberAccessExpressionSyntax texAccess)
            {
                ExpressionSyntax textureInstance = texAccess.Expression;

                // HLSL requires int3(coord, mipLevel)
                if (methodSymbol.Name == "Load")
                {
                    Visit(textureInstance);
                    hlsl.Append(".Load(int3(");
                    AppendArgument(node.ArgumentList.Arguments[0]);
                    hlsl.Append(", ");
                    if (node.ArgumentList.Arguments.Count > 1)
                        AppendArgument(node.ArgumentList.Arguments[1]);
                    else
                        hlsl.Append('0');
                    hlsl.Append("))");
                    return;
                }

                // Sample / SampleLevel
                Visit(textureInstance);
                hlsl.Append('.');
                hlsl.Append(methodSymbol.Name);
                hlsl.Append("(DivisionDefaultSampler");
                for (int i = 0; i < node.ArgumentList.Arguments.Count; i++)
                {
                    hlsl.Append(", ");
                    AppendArgument(node.ArgumentList.Arguments[i]);
                }
                hlsl.Append(')');
                return;
            }

            // Struct method calls
            if (node.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                string methodName = memberAccess.Name.Identifier.Text;
                ExpressionSyntax instance = memberAccess.Expression;

                if (methodSymbol?.ContainingType?.TypeKind == TypeKind.Struct)
                {
                    hlsl.Append($"{methodName}(");
                    Visit(instance);
                    if (node.ArgumentList.Arguments.Count > 0)
                    {
                        hlsl.Append(", ");
                        for (int i = 0; i < node.ArgumentList.Arguments.Count; i++)
                        {
                            if (i > 0) hlsl.Append(", ");
                            AppendArgument(node.ArgumentList.Arguments[i]);
                        }
                    }
                    hlsl.Append(')');
                    return;
                }
            }

            // Free functions (math.*, inline shader functions)
            SymbolInfo symbolInfo = semanticModel.GetSymbolInfo(node);
            if (symbolInfo.Symbol is IMethodSymbol method)
            {
                // Static helper classes (ShaderMath, PBR, SDFPrimitives, ...) are
                // translated on demand into HLSL functions and called by their
                // mangled name, because HLSL has no namespaces or overloads.
                if (IsExternalHelperMethod(method))
                {
                    hlsl.Append(EnsureHelperMethod(method));
                    hlsl.Append('(');
                    for (int i = 0; i < node.ArgumentList.Arguments.Count; i++)
                    {
                        if (i > 0) hlsl.Append(", ");
                        AppendArgument(node.ArgumentList.Arguments[i]);
                    }
                    hlsl.Append(')');
                    return;
                }

                hlsl.Append(method.Name);
                hlsl.Append('(');
                for (int i = 0; i < node.ArgumentList.Arguments.Count; i++)
                {
                    if (i > 0) hlsl.Append(", ");
                    AppendArgument(node.ArgumentList.Arguments[i]);
                }
                hlsl.Append(')');
            }
            else
            {
                // Fallback: math. pattern from raw syntax
                if (node.Expression is MemberAccessExpressionSyntax fallback &&
                    fallback.Expression.ToString() == "math")
                {
                    hlsl.Append(fallback.Name.Identifier.Text);
                    hlsl.Append('(');
                    for (int i = 0; i < node.ArgumentList.Arguments.Count; i++)
                    {
                        if (i > 0) hlsl.Append(", ");
                        AppendArgument(node.ArgumentList.Arguments[i]);
                    }
                    hlsl.Append(')');
                }
                else hlsl.Append(node.ToString());
            }
        }

        /// <summary>
        /// Translates element access expressions like "buffer[index]" or "texture[coord]".
        /// </summary>
        public override void VisitElementAccessExpression(ElementAccessExpressionSyntax node)
        {
            Visit(node.Expression); // Buffer / texture name
            hlsl.Append('[');
            for (int i = 0; i < node.ArgumentList.Arguments.Count; i++) // Handle multiple indices
            {
                if (i > 0) hlsl.Append(", ");
                AppendArgument(node.ArgumentList.Arguments[i]);
            }
            hlsl.Append(']');
        }

        /// <summary>
        /// Translates ternary / conditional expressions.
        /// </summary>
        public override void VisitConditionalExpression(ConditionalExpressionSyntax node)
        {
            Visit(node.Condition);
            hlsl.Append(" ? ");
            Visit(node.WhenTrue);
            hlsl.Append(" : ");
            Visit(node.WhenFalse);
        }

        /// <summary>
        /// Translates cast expressions like "(int)id.x".
        /// </summary>
        public override void VisitCastExpression(CastExpressionSyntax node)
        {
            ITypeSymbol? targetType = semanticModel.GetTypeInfo(node.Type).Type;
            string hlslType = ConvertType(targetType);
            hlsl.Append($"({hlslType})");
            Visit(node.Expression);
        }

        /// <summary>
        /// Translates assignment expressions.
        /// </summary>
        public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
        {
            Visit(node.Left);
            hlsl.Append($" {node.OperatorToken.Text} ");
            Visit(node.Right);
        }

        /// <summary>
        /// Translates operator expressions like +, -, *, /, etc.
        /// </summary>
        public override void VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            Visit(node.Left);
            hlsl.Append($" {node.OperatorToken.Text} ");
            Visit(node.Right);
        }

        /// <summary>
        /// Translates parentheses around expressions.
        /// </summary>
        public override void VisitParenthesizedExpression(ParenthesizedExpressionSyntax node)
        {
            hlsl.Append('(');
            Visit(node.Expression);
            hlsl.Append(')');
        }

        /// <summary>
        /// Translates prefix unary expressions.
        /// </summary>
        public override void VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
        {
            hlsl.Append(node.OperatorToken.Text);
            Visit(node.Operand);
        }

        /// <summary>
        /// Translates postfix unary expressions.
        /// </summary>
        public override void VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
        {
            Visit(node.Operand);
            hlsl.Append(node.OperatorToken.Text);
        }

        /// <summary>
        /// Translates member access expressions "id.x" or "pos.y".
        /// </summary>
        public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            string memberName = node.Name.Identifier.Text;

            // float.MaxValue and friends have no HLSL member equivalent; the
            // predefined type name would otherwise be dropped and leave a bare
            // ".MaxValue" in the output.
            if (node.Expression is PredefinedTypeSyntax predefined &&
                memberName is "MaxValue" or "MinValue" or "Epsilon")
            {
                hlsl.Append(PredefinedConstant(predefined.Keyword.Text, memberName));
                return;
            }

            // Helper-class constants (PBR.RECIPROCAL_PI) and enum members are static
            // fields; HLSL has neither namespaces nor enums, so inline them.
            if (semanticModel.GetSymbolInfo(node).Symbol is IFieldSymbol staticField && staticField.IsStatic)
            {
                if (staticField.ContainingType.TypeKind == TypeKind.Enum && staticField.HasConstantValue && staticField.ConstantValue is not null)
                {
                    hlsl.Append(LiteralText(staticField.ConstantValue, "int"));
                    return;
                }

                if (IsExternalHelperType(staticField.ContainingType))
                {
                    hlsl.Append(EnsureHelperConstant(staticField));
                    return;
                }
            }

            // StructuredBuffer exposes GetDimensions, Load, and operator[], not a
            // Length/Count property. Route the C# convenience properties through a
            // generated helper so the shader author can keep using buffer.Length.
            if (memberName is "Length" or "Count" &&
                TryGetStructuredBufferField(node.Expression, out string fieldName, out string bufferTypeName))
            {
                EnsureBufferLengthHelper(fieldName, bufferTypeName);
                hlsl.Append($"DivisionBufferLength_{fieldName}(");
                Visit(node.Expression);
                hlsl.Append(')');
                return;
            }

            Visit(node.Expression);
            hlsl.Append($".{memberName}");
        }

        /// <summary>
        /// Maps a framework numeric limit (float.MaxValue, int.MinValue, ...) onto the
        /// HLSL literal with the same value.
        /// </summary>
        private static string PredefinedConstant(string typeName, string member)
        {
            return (typeName, member) switch
            {
                ("float", "MaxValue") => "3.402823466e+38",
                ("float", "MinValue") => "-3.402823466e+38",
                ("float", "Epsilon") => "1.401298464e-45",
                ("double", "MaxValue") => "1.7976931348623157e+308",
                ("double", "MinValue") => "-1.7976931348623157e+308",
                ("double", "Epsilon") => "4.9406564584124654e-324",
                ("int", "MaxValue") => "2147483647",
                ("int", "MinValue") => "(-2147483647 - 1)",
                ("uint", "MaxValue") => "4294967295u",
                ("long", "MaxValue") => "9223372036854775807L",
                ("long", "MinValue") => "(-9223372036854775807L - 1L)",
                ("ulong", "MaxValue") => "18446744073709551615UL",
                _ => "0"
            };
        }

        /// <summary>
        /// Translates the "default" literal into a zero-initialized value.
        /// </summary>
        public override void VisitLiteralExpression(LiteralExpressionSyntax node)
        {
            string text = node.Token.Text;

            if (node.IsKind(SyntaxKind.DefaultLiteralExpression))
            {
                AppendDefaultValue(semanticModel.GetTypeInfo(node).ConvertedType);
                return;
            }

            // HLSL reads an unsuffixed whole number as an int, so dropping the C#
            // float suffix from "1f" would silently turn "1f / 6f" into integer
            // division. Keep the value a float by always emitting a decimal point.
            // Hexadecimal integers can end in F too (0xFF), so only a literal whose
            // value is actually floating point gets the suffix treatment.
            bool isFloatingPointLiteral = node.Token.Value is float or double or decimal;
            if (isFloatingPointLiteral && (text.EndsWith('f') || text.EndsWith('F')))
            {
                text = text.TrimEnd('f', 'F');
                if (!text.Contains('.') && !text.Contains('e') && !text.Contains('E'))
                    text += ".0";
            }

            hlsl.Append(text);
        }

        #endregion ExpressionVisitors
        #region MiscVisitors

        /// <summary>
        /// Translates a block of code " { } ".
        /// </summary>
        public override void VisitBlock(BlockSyntax node)
        {
            WriteIndent();
            hlsl.AppendLine("{");
            indentLvl++;
            foreach (StatementSyntax statement in node.Statements) Visit(statement);
            indentLvl--;
            WriteIndent();
            hlsl.AppendLine("}");
        }

        /// <summary>
        /// Translates outputting the variable/parameter name.
        /// </summary>
        public override void VisitIdentifierName(IdentifierNameSyntax node)
        {
            // Inside a helper class, a bare constant reference (for example "Gain")
            // belongs to that class and needs its mangled name. Looking at the current
            // helper's members keeps this O(1); a semantic lookup per identifier would
            // be far too slow for a large shader.
            if (currentHelperType is not null)
            {
                foreach (ISymbol member in currentHelperType.GetMembers(node.Identifier.Text))
                {
                    if (member is IFieldSymbol field && field.IsStatic && field.HasConstantValue)
                    {
                        hlsl.Append(EnsureHelperConstant(field));
                        return;
                    }
                }
            }

            hlsl.Append(node.Identifier.Text);
        }

        /// <summary>
        /// Translates arguments eg. "id.x" or "pos.y".
        /// </summary>
        public override void VisitArgument(ArgumentSyntax node)
        {
            Visit(node.Expression);
        }

        #endregion MiscVisitors
        #region OutsideStructTranslation

        /// <summary>
        /// Translates an outside struct if necessary, to preserve CPU - GPU parity.
        /// </summary>
        private void TranslateStructIfNeeded(INamedTypeSymbol structSymbol)
        {
            string structName = structSymbol.Name;

            // Prefer the runtime type when it is known. Reflection gives us the
            // actual field order and layout, and works even when the struct is
            // declared outside the shader's source file.
            if (knownTypes.TryGetValue(structName, out Type? runtimeType))
            {
                TranslateRuntimeStructIfNeeded(runtimeType);
                return;
            }

            if (processedStructs.Contains(structName)) return;
            if (IsBuiltInType(structName)) return; // Skip built-in types
            processedStructs.Add(structName);

            foreach (SyntaxReference syntaxRef in structSymbol.DeclaringSyntaxReferences)
            {
                if (syntaxRef.GetSyntax() is StructDeclarationSyntax structDecl)
                {
                    TranslateStructDeclaration(structDecl);
                    break;
                }
            }
        }

        /// <summary>
        /// Emits an HLSL struct declaration from a runtime type, recursively emitting
        /// nested structs first. Built-in scalar/vector types are skipped.
        /// </summary>
        private void TranslateRuntimeStructIfNeeded(Type type)
        {
            if (ShaderStructLayout.IsBuiltIn(type) || type.IsPrimitive || type.IsEnum)
                return;

            string structName = ShaderStructLayout.GetHlslTypeName(type);
            if (!processedStructs.Add(structName)) return;

            // Emit nested element structs before this one so the generated HLSL
            // never references a struct that has not been declared yet.
            foreach (FieldInfo field in ShaderStructLayout.GetSerializableFields(type))
                TranslateRuntimeStructIfNeeded(field.FieldType);

            structDeclarations.Add(ShaderStructLayout.BuildStructDeclaration(type));
        }

        /// <summary>
        /// Translates an outside struct declartion to a GPU compliant type definition.
        /// </summary>
        private void TranslateStructDeclaration(StructDeclarationSyntax structDecl)
        {
            StringBuilder structBuilder = new StringBuilder();
            structBuilder.AppendLine($"struct {structDecl.Identifier.Text}");
            structBuilder.AppendLine("{");

            // Skip methods inside structs - only translate fields
            foreach (FieldDeclarationSyntax field in structDecl.Members.OfType<FieldDeclarationSyntax>())
            {
                TypeSyntax fieldType = field.Declaration.Type;
                string fieldName = field.Declaration.Variables.First().Identifier.Text;
                ITypeSymbol? typeSymbol = semanticModel.GetTypeInfo(fieldType).Type;
                string hlslType = ConvertType(typeSymbol);

                if (typeSymbol is INamedTypeSymbol namedType && namedType.TypeKind == TypeKind.Struct && !IsBuiltInType(namedType.Name))
                    TranslateStructIfNeeded(namedType);
                structBuilder.AppendLine($"    {hlslType} {fieldName};");
            }

            structBuilder.AppendLine("};");
            structDeclarations.Add(structBuilder.ToString());
        }

        #endregion OutsideStructTranslation
        #region InlineFunctionTranslation

        /// <summary>
        /// Emits a <c>const</c> or <c>static readonly</c> shader struct field as an HLSL
        /// <c>static const</c>. Shader code commonly stores tuning values this way
        /// (shadow epsilon, pi, reciprocal pi, ...), and HLSL needs them declared.
        /// </summary>
        private void CollectConstantField(FieldDeclarationSyntax field)
        {
            foreach (VariableDeclaratorSyntax variable in field.Declaration.Variables)
            {
                if (variable.Initializer is null)
                    throw new NotSupportedException(
                        $"Static shader field '{variable.Identifier.Text}' has no initializer. " +
                        "Shader constants must be initialized with a compile-time value.");

                ShaderStructLayout.ValidateHlslIdentifier(variable.Identifier.Text, $"shader constant '{variable.Identifier.Text}'");

                ITypeSymbol? typeSymbol = semanticModel.GetTypeInfo(field.Declaration.Type).Type;
                string hlslType = ConvertType(typeSymbol);
                string value = EvaluateConstantText(variable.Initializer.Value, semanticModel, hlslType);

                constantDeclarations.Add($"static const {hlslType} {variable.Identifier.Text} = {value};");
            }
        }

        /// <summary>
        /// Renders a compile-time constant expression as HLSL literal text, falling
        /// back to translating the expression when Roslyn cannot fold it.
        /// </summary>
        private string EvaluateConstantText(ExpressionSyntax expression, SemanticModel model, string hlslType)
        {
            Optional<object?> constant = model.GetConstantValue(expression);
            if (constant.HasValue && constant.Value is not null)
                return LiteralText(constant.Value, hlslType);

            _ = hlslType;
            return TranslateExpression(expression, model);
        }

        /// <summary>
        /// Formats a CLR constant as an HLSL literal.
        /// </summary>
        private static string LiteralText(object value, string hlslType)
        {
            string text = value switch
            {
                float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                bool b => b ? "true" : "false",
                _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "0",
            };

            // HLSL reads a whole number without a decimal point as an int, which would
            // turn constants like "1f / 6f" into integer division.
            bool isFloatType = hlslType is "float" or "float2" or "float3" or "float4" or "half";
            if (isFloatType && text.Length > 0 && !text.Contains('.') && !text.Contains('e') && !text.Contains('E'))
                text += ".0";

            return text;
        }

        /// <summary>
        /// Translates a single expression to HLSL text without disturbing the main
        /// output buffer.
        /// </summary>
        private string TranslateExpression(ExpressionSyntax expression, SemanticModel model)
        {
            SemanticModel savedModel = semanticModel;
            string savedHlsl = hlsl.ToString();
            int savedIndent = indentLvl;

            try
            {
                semanticModel = model;
                hlsl.Clear();
                indentLvl = 0;
                Visit(expression);
                return hlsl.ToString();
            }
            finally
            {
                semanticModel = savedModel;
                hlsl.Clear();
                hlsl.Append(savedHlsl);
                indentLvl = savedIndent;
            }
        }

        /// <summary>
        /// Translates a non-kernel method to an HLSL inline function.
        /// </summary>
        private void TranslateInlineFunction(MethodDeclarationSyntax method)
            => inlineFunctions.Add(EmitFunction(method, method.Identifier.Text));

        /// <summary>
        /// Emits an HLSL function for a C# method and registers its prototype so the
        /// definition order does not matter.
        /// </summary>
        /// <param name="method">The C# method to translate.</param>
        /// <param name="emittedName">The HLSL function name to use.</param>
        private string EmitFunction(MethodDeclarationSyntax method, string emittedName)
        {
            StringBuilder builder = new StringBuilder();
            ITypeSymbol? returnType = semanticModel.GetDeclaredSymbol(method)?.ReturnType;
            string hlslReturnType = ConvertType(returnType);
            List<string> parameters = [];

            // Parameter setup
            foreach (ParameterSyntax param in method.ParameterList.Parameters)
            {
                ShaderStructLayout.ValidateHlslIdentifier(
                    param.Identifier.Text, "parameter '" + param.Identifier.Text + "'");

                ITypeSymbol? paramType = semanticModel.GetTypeInfo(param.Type!).Type;
                string hlslParamType = ConvertType(paramType);
                parameters.Add($"{ParameterModifier(param)}{hlslParamType} {param.Identifier.Text}");
            }

            // Function signature
            string signature = $"{hlslReturnType} {emittedName}({string.Join(", ", parameters)})";
            inlineFunctionSignatures.Add(signature);
            builder.Append(signature);
            builder.AppendLine();
            builder.AppendLine("{");

            // HLSL reads parameters by value, but an expression-bodied C# method has
            // no statement block; reject those with a clear message instead of
            // dereferencing a null body.
            if (method.Body is null)
                throw new NotSupportedException(
                    $"Shader function '{method.Identifier.Text}' uses an expression body. " +
                    "Use a block body with an explicit return statement in shader code.");

            // Temporarily store current HLSL content and switch to new builder for the body
            string originalHlsl = hlsl.ToString();
            hlsl.Clear();
            int oldIndent = indentLvl;
            indentLvl = 1;

            foreach (StatementSyntax statement in method.Body.Statements) Visit(statement);

            string body = hlsl.ToString();
            builder.Append(body);

            hlsl.Clear(); // Restore original HLSL
            hlsl.Append(originalHlsl);
            indentLvl = oldIndent;

            builder.AppendLine("}");
            return builder.ToString();
        }

        /// <summary>
        /// Maps a C# parameter modifier to its HLSL equivalent. HLSL has no <c>ref</c>,
        /// so <c>ref</c> becomes <c>inout</c>.
        /// </summary>
        private static string ParameterModifier(ParameterSyntax param)
        {
            if (param.Modifiers.Any(modifier => modifier.Text == "out")) return "out ";
            if (param.Modifiers.Any(modifier => modifier.Text == "ref")) return "inout ";
            return string.Empty;
        }
        /// <summary>
        /// Discovers all non-kernel methods (inline functions) in the shader struct.
        /// </summary>
        private static List<MethodDeclarationSyntax> FindAllInlineFunctions(StructDeclarationSyntax shaderStruct)
        {
            List<MethodDeclarationSyntax> functions = [];
            foreach (MethodDeclarationSyntax method in shaderStruct.Members.OfType<MethodDeclarationSyntax>())
            {
                bool hasKernelAttr = method.AttributeLists
                    .SelectMany(list => list.Attributes)
                    .Any(attr => attr.Name.ToString() == "Kernel");
                if (!hasKernelAttr) functions.Add(method);
            }
            return functions;
        }

        #endregion InlineFunctionTranslation
        #region HelperFunctions

        /// <summary>
        /// Gets the thread counts from the Kernel attribute.
        /// </summary>
        /// <param name="method">Method syntax attribute is applied to</param>
        private void GetThreadCounts(MethodDeclarationSyntax method)
        {
            AttributeSyntax? kernelAttr = method.AttributeLists
                .SelectMany(list => list.Attributes)
                .FirstOrDefault(attr => attr.Name.ToString() == "Kernel");
            if (kernelAttr?.ArgumentList == null) return; // Check if found Kernel attribute

            foreach (var arg in kernelAttr.ArgumentList.Arguments) // Parse named arguments
            {
                if (arg.NameEquals != null)
                {
                    // Named arguments, ie. ThreadsX = 8
                    string name = arg.NameEquals.Name.ToString();
                    string value = arg.Expression.ToString();

                    switch (name)
                    {
                        case "ThreadsX":
                            threadsX = int.Parse(value);
                            break;
                        case "ThreadsY":
                            threadsY = int.Parse(value);
                            break;
                        case "ThreadsZ":
                            threadsZ = int.Parse(value);
                            break;
                    }
                }
                else if (arg.Expression is LiteralExpressionSyntax literal)
                {
                    // Positional arguments, ie. [Kernel(8, 8, 1)]
                    int index = kernelAttr.ArgumentList.Arguments.IndexOf(arg);
                    int value = int.Parse(literal.Token.Text);

                    switch (index)
                    {
                        case 0:
                            threadsX = value;
                            break;
                        case 1:
                            threadsY = value;
                            break;
                        case 2:
                            threadsZ = value;
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// Handle C# primitive types that have different names in HLSL.
        /// </summary>
        /// <param name="typeSymbol">C# type symbol</param>
        /// <returns>HLSL type symbol syntax</returns>
        private static string ConvertType(ITypeSymbol? typeSymbol)
        {
            if (typeSymbol == null) return "unknown";

            if (typeSymbol.TypeKind == TypeKind.Enum && typeSymbol is INamedTypeSymbol enumType)
                return ConvertType(enumType.EnumUnderlyingType);

            if (typeSymbol.Name is "Vector2" or "Vector3" or "Vector4" &&
                typeSymbol.ContainingNamespace?.ToDisplayString() == "System.Numerics")
            {
                return $"float{typeSymbol.Name[^1]}";
            }

            return typeSymbol.SpecialType switch
            {
                SpecialType.System_Single => "float",
                SpecialType.System_Double => "double",
                SpecialType.System_Int32 => "int",
                SpecialType.System_Void => "void",
                SpecialType.System_UInt32 => "uint",
                SpecialType.System_Boolean => "bool",
                _ => typeSymbol.Name // float3, int2, etc.
            };
        }

        /// <summary>
        /// Gathers all fields in the shader struct.
        /// </summary>
        /// <remarks>
        /// Plain (non-<c>[ShaderResource]</c>) instance fields become shader uniforms.
        /// When the runtime shader type is known, their declarations come from
        /// <see cref="ShaderUniformLayout"/> so the emitted HLSL and the CPU-side
        /// packing are built from the same description.
        /// </remarks>
        private void CollectFields(StructDeclarationSyntax structDeclaration)
        {
            foreach (FieldDeclarationSyntax field in structDeclaration.Members.OfType<FieldDeclarationSyntax>())
            {
                // const fields are implicitly static in C# but carry no "static"
                // modifier, so both spellings have to be checked.
                if (field.Modifiers.Any(modifier => modifier.Text is "static" or "const"))
                {
                    CollectConstantField(field);
                    continue;
                }

                bool isShaderResource = field.AttributeLists // Check if this field has ShaderResource attribute
                    .SelectMany(list => list.Attributes)
                    .Any(attr => attr.Name.ToString() == "ShaderResource");

                TypeSyntax fieldType = field.Declaration.Type;
                string fieldName = field.Declaration.Variables.First().Identifier.Text;
                shaderFields.TryGetValue(fieldName, out FieldInfo? runtimeField);

                if (isShaderResource) // This is a GPU resource (buffer, texture, etc.)
                {
                    string? hlslDeclaration = TranslateResourceField(fieldType, fieldName, runtimeField);
                    if (hlslDeclaration != null) resourceDeclarations.Add(hlslDeclaration);
                }
                else if (shaderType is null)
                {
                    // Fallback for callers that translate without a runtime type. The
                    // normal path builds uniforms from metadata in Translate().
                    string? hlslDeclaration = TranslateVariableField(fieldType, fieldName);
                    if (hlslDeclaration != null) variableDeclarations.Add(hlslDeclaration);
                }
            }
        }

        /// <summary>
        /// Builds the HLSL declarations for the shader's uniforms. When the runtime
        /// type is known the declarations come from the shared
        /// <see cref="ShaderUniformLayout"/>, so HLSL and the CPU-side packing cannot
        /// disagree; otherwise the syntax-only fallback is used.
        /// </summary>
        private List<string> GetUniformDeclarations()
        {
            if (shaderType is null) return variableDeclarations;

            ShaderUniformLayout layout = ShaderUniformLayout.For(shaderType);
            return
            [
                .. layout.Fields.Select(field =>
                    $"{ShaderUniformLayout.GetHlslTypeName(field.Kind)} {field.Name};")
            ];
        }

        /// <summary>
        /// Translates resource fields from C# to HLSL.
        /// </summary>
        /// <param name="fieldType">C# resource field type syntax</param>
        /// <param name="fieldName">Field name</param>
        /// <param name="runtimeField">Optional metadata for the field, used to resolve element structs that live outside the shader source file.</param>
        /// <returns>HLSL resouce field type syntax</returns>
        /// <exception cref="NotSupportedException">If the type is not supported as a shader resource</exception>
        private string? TranslateResourceField(TypeSyntax fieldType, string fieldName, FieldInfo? runtimeField)
        {
            ShaderStructLayout.ValidateHlslIdentifier(fieldName, $"shader resource '{fieldName}'");

            if (fieldType is IdentifierNameSyntax identifier) // Test for sampler type
            {
                if (identifier.Identifier.Text == "Sampler") return $"SamplerState {fieldName};";
                throw new NotSupportedException($"ShaderResource field '{fieldName}' is '{identifier.Identifier.Text}'. " +
                    $"Only 'Sampler' is allowed as a non-generic resource type.");
            }

            if (fieldType is GenericNameSyntax generic) // Test for generic types
            {
                string genericName = generic.Identifier.Text;

                // Resolve the element type from metadata when possible. This is what
                // allows StructuredBuffer<MyDto> to work when MyDto is declared in a
                // different source file (or a different assembly) than the shader.
                Type? runtimeElementType = null;
                if (runtimeField is not null && runtimeField.FieldType.IsGenericType)
                {
                    Type[] typeArguments = runtimeField.FieldType.GetGenericArguments();
                    if (typeArguments.Length == 1)
                        runtimeElementType = typeArguments[0];
                }

                string elementType;
                if (runtimeElementType is not null)
                {
                    if (genericName is "StructuredBuffer" or "RWStructuredBuffer")
                    {
                        ShaderStructLayout.ValidateElementType(runtimeElementType);
                        TranslateRuntimeStructIfNeeded(runtimeElementType);
                    }

                    elementType = ShaderStructLayout.GetHlslTypeName(runtimeElementType);
                }
                else
                {
                    TypeSyntax typeArg = generic.TypeArgumentList.Arguments[0];
                    ITypeSymbol? typeSymbol = semanticModel.GetTypeInfo(typeArg).Type;
                    elementType = ConvertType(typeSymbol);

                    // If StructuredBuffer, translate the element struct
                    if ((genericName == "StructuredBuffer" || genericName == "RWStructuredBuffer") &&
                        typeSymbol is INamedTypeSymbol namedType && namedType.TypeKind == TypeKind.Struct)
                        TranslateStructIfNeeded(namedType);
                }

                return genericName switch // Map to HLSL
                {
                    "Buffer" => $"Buffer<{elementType}> {fieldName};",
                    "RWBuffer" => $"RWBuffer<{elementType}> {fieldName};",
                    "StructuredBuffer" => $"StructuredBuffer<{elementType}> {fieldName};",
                    "RWStructuredBuffer" => $"RWStructuredBuffer<{elementType}> {fieldName};",
                    "Texture2D" => $"Texture2D<{elementType}> {fieldName};",
                    "RWTexture2D" => $"RWTexture2D<{elementType}> {fieldName};",
                    "Texture3D" => $"Texture3D<{elementType}> {fieldName};",
                    "RWTexture3D" => $"RWTexture3D<{elementType}> {fieldName};",
                    _ => throw new NotSupportedException($"Unknown generic resource type: {genericName}. " +
                    $"Supported types: Buffer, RWBuffer, StructuredBuffer, RWStructuredBuffer, Texture2D, RWTexture2D, Texture3D, RWTexture3D")
                };
            }

            throw new NotSupportedException($"ShaderResource field '{fieldName}' must be a generic type " +
                $"(Buffer<T>, Texture2D<T>, etc.) or Sampler. Got: {fieldType.GetType().Name}");
        }

        /// <summary>
        /// Resolves a member-access receiver to a structured-buffer field and builds
        /// the HLSL type used by the generated length helper.
        /// </summary>
        private bool TryGetStructuredBufferField(
            ExpressionSyntax expression,
            out string fieldName,
            out string bufferTypeName)
        {
            fieldName = string.Empty;
            bufferTypeName = string.Empty;

            string? candidateName = expression switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.Text,
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } member => member.Name.Identifier.Text,
                _ => null,
            };

            if (candidateName is null)
                return false;

            if (shaderFields.TryGetValue(candidateName, out FieldInfo? runtimeField))
            {
                Type fieldType = runtimeField.FieldType;
                if (!fieldType.IsGenericType)
                    return false;

                Type definition = fieldType.GetGenericTypeDefinition();
                if (definition != typeof(StructuredBuffer<>) &&
                    definition != typeof(RWStructuredBuffer<>))
                {
                    return false;
                }

                string genericName = definition.Name[..definition.Name.IndexOf('`')];
                Type elementType = fieldType.GetGenericArguments()[0];
                fieldName = candidateName;
                bufferTypeName = $"{genericName}<{ShaderStructLayout.GetHlslTypeName(elementType)}>";
                return true;
            }

            // Fallback for callers that use the translator without a runtime type.
            if (semanticModel.GetSymbolInfo(expression).Symbol is not IFieldSymbol fieldSymbol ||
                fieldSymbol.Type is not INamedTypeSymbol namedType ||
                namedType.TypeArguments.Length != 1 ||
                namedType.Name is not ("StructuredBuffer" or "RWStructuredBuffer"))
            {
                return false;
            }

            fieldName = fieldSymbol.Name;
            bufferTypeName = $"{namedType.Name}<{ConvertType(namedType.TypeArguments[0])}>";
            return true;
        }

        /// <summary>
        /// Generates the HLSL helper that implements the C# Length/Count property
        /// through StructuredBuffer.GetDimensions.
        /// </summary>
        private void EnsureBufferLengthHelper(string fieldName, string bufferTypeName)
        {
            if (bufferLengthHelpers.ContainsKey(fieldName))
                return;

            bufferLengthHelpers[fieldName] =
                $"int DivisionBufferLength_{fieldName}({bufferTypeName} buffer)\n" +
                "{\n" +
                "    uint count;\n" +
                "    uint stride;\n" +
                "    buffer.GetDimensions(count, stride);\n" +
                "    return (int)count;\n" +
                "}\n";
        }

        private static Dictionary<string, FieldInfo> BuildShaderFields(Type? shaderType)
        {
            var fields = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
            if (shaderType is null)
                return fields;

            foreach (FieldInfo field in shaderType.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                fields[field.Name] = field;
            }

            return fields;
        }

        private static Dictionary<string, Type> BuildKnownTypes(Type? shaderType)
        {
            var types = new Dictionary<string, Type>(StringComparer.Ordinal);
            if (shaderType is null)
                return types;

            foreach (FieldInfo field in shaderType.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                AddType(field.FieldType);
            }

            return types;

            void AddType(Type type)
            {
                if (type.IsGenericType)
                {
                    foreach (Type argument in type.GetGenericArguments())
                        AddType(argument);
                    return;
                }

                if (type.IsArray)
                {
                    if (type.GetElementType() is Type elementType)
                        AddType(elementType);
                    return;
                }

                if (type.IsPointer || type.IsByRef || type.IsPrimitive || type == typeof(string))
                    return;

                if (type.IsEnum)
                {
                    types.TryAdd(type.Name, type);
                    return;
                }

                if (!type.IsValueType)
                    return;

                if (types.TryGetValue(type.Name, out Type? existingType))
                {
                    if (existingType != type)
                        throw new NotSupportedException(
                            $"Two structured-buffer element types are named '{type.Name}'. " +
                            "HLSL has no namespaces, so element and nested struct names must be unique.");

                    return;
                }

                types[type.Name] = type;

                if (ShaderStructLayout.IsBuiltIn(type))
                    return;

                foreach (FieldInfo field in type.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    AddType(field.FieldType);
                }
            }
        }

        /// <summary>
        /// Translates a variable type field to its corresponding HLSL type.
        /// </summary>
        /// <param name="fieldType">Type syntax of the C# type</param>
        /// <param name="fieldName">Field name</param>
        /// <returns>HLSL variable type syntax</returns>
        private string? TranslateVariableField(TypeSyntax fieldType, string fieldName)
        {
            ITypeSymbol? typeSymbol = semanticModel.GetTypeInfo(fieldType).Type;
            string hlslType = ConvertType(typeSymbol);

            // Only allow basic types and vector types
            if (IsBuiltInType(hlslType)) return $"{hlslType} {fieldName};";
            return null;
        }

        /// <summary>
        /// Checks if a type is a built-in HLSL type (does not need custom struct translation)
        /// </summary>
        private static bool IsBuiltInType(string typeName)
        {
            // Scalars
            string[] scalars = ["float", "int", "uint", "double", "bool", "half"];
            if (scalars.Contains(typeName)) return true;

            // Vectors (float2, float3, float4, int2, int3, int4, etc.)
            string[] prefixes = ["float", "int", "uint", "double", "bool", "half"];
            string[] suffixes = ["2", "3", "4"];

            foreach (string prefix in prefixes)
            {
                foreach (string suffix in suffixes)
                {
                    if (typeName == $"{prefix}{suffix}")
                        return true;
                }
            }

            // Matrices (float2x2, float3x2, float2x4, int3x3, etc.)
            foreach (string prefix in prefixes)
            {
                foreach (string suffix in suffixes)
                {
                    foreach (string suffix2 in suffixes)
                    {
                        if (typeName == $"{prefix}{suffix}x{suffix2}")
                            return true;
                    }
                }
            }
            return false;
        }

        // Locals created for out-argument declarations, keyed by the argument syntax.
        private readonly Dictionary<ArgumentSyntax, string> outArgumentLocals = [];

        // Semantic models are expensive to build for the whole project source set, so
        // they are cached per syntax tree. Without this, every helper call in a large
        // shader rebuilt the model, turning SDFShader3D's translation into minutes.
        private readonly Dictionary<SyntaxTree, SemanticModel> helperSemanticModels = [];

        private SemanticModel GetHelperSemanticModel(SyntaxTree tree)
        {
            if (helperSemanticModels.TryGetValue(tree, out SemanticModel? cached))
                return cached;

            SemanticModel model = compilation!.GetSemanticModel(tree);
            helperSemanticModels[tree] = model;
            return model;
        }
        private int outDiscardCounter;

        /// <summary>
        /// Writes an indentation to the HLSL string builder.
        /// </summary>
        /// <summary>
        /// True when a method belongs to a shared static helper class (for example
        /// <c>ShaderMath</c> or <c>PBR</c>) that has to be translated on demand,
        /// rather than to the shader struct or to DivisionMath's intrinsic class.
        /// </summary>
        private bool IsExternalHelperMethod(IMethodSymbol method)
        {
            if (!method.IsStatic) return false;
            if (method.MethodKind != MethodKind.Ordinary) return false;
            return IsExternalHelperType(method.ContainingType);
        }

        /// <summary>
        /// True when the type is a user-defined helper class whose methods are emitted
        /// into the shader. HLSL has no namespaces, so their methods are translated and
        /// name-mangled. DivisionMath's <c>math</c> class and framework types are
        /// excluded: the former maps to HLSL intrinsics, the latter must be replaced
        /// with <c>math.*</c> calls in shader source.
        /// </summary>
        private bool IsExternalHelperType(INamedTypeSymbol? type)
        {
            if (type is null) return false;
            if (type.TypeKind != TypeKind.Class) return false;

            string? namespaceName = type.ContainingNamespace?.ToDisplayString();
            if (namespaceName is "DivisionEngine.MathLib" && type.Name == "math") return false;
            if (namespaceName is not null && namespaceName.StartsWith("System", StringComparison.Ordinal)) return false;

            string? shaderFullName = shaderType?.FullName;
            if (shaderFullName is not null && type.ToDisplayString() == shaderFullName) return false;

            return true;
        }

        /// <summary>
        /// Returns the HLSL function name for a helper method, translating its body the
        /// first time it is referenced. Overloads and same-named methods from different
        /// classes get distinct mangled names because HLSL supports neither namespaces
        /// nor overloads.
        /// </summary>
        private string EnsureHelperMethod(IMethodSymbol method)
        {
            string key = HelperKey(method);
            if (helperMethodNames.TryGetValue(key, out string? existing)) return existing;

            string baseName = method.ContainingType.Name + "_" + method.Name;
            string name = baseName;
            int suffix = 2;
            while (!emittedNames.Add(name))
                name = baseName + "_" + suffix++;

            helperMethodNames[key] = name;

            // Guard against recursive helper calls (A calls B calls A) while a body is
            // still being translated: the prototype above makes the call valid anyway.
            if (!helperMethodsInProgress.Add(key)) return name;

            try
            {
                if (compilation is null)
                    throw new NotSupportedException(
                        $"Shader calls helper method '{method.ContainingType.Name}.{method.Name}'. " +
                        "Translating shared helper classes requires the project sources; compile the shader " +
                        "through DivisionShaderCompiler or DivisionComputeContext.");

                if (method.DeclaringSyntaxReferences.FirstOrDefault() is not SyntaxReference reference ||
                    reference.GetSyntax() is not MethodDeclarationSyntax declaration)
                {
                    throw new NotSupportedException(
                        $"Could not find the source of helper method '{method.ContainingType.Name}.{method.Name}'. " +
                        "Helper classes must be compiled from source in the same project as the shader.");
                }

                SemanticModel helperModel = GetHelperSemanticModel(declaration.SyntaxTree);
                SemanticModel savedModel = semanticModel;
                INamedTypeSymbol? savedHelperType = currentHelperType;
                try
                {
                    semanticModel = helperModel;
                    currentHelperType = method.ContainingType;
                    helperMethodBodies[name] = EmitFunction(declaration, name);
                }
                finally
                {
                    semanticModel = savedModel;
                    currentHelperType = savedHelperType;
                }
            }
            finally
            {
                helperMethodsInProgress.Remove(key);
            }

            return name;
        }

        /// <summary>
        /// Returns the HLSL <c>static const</c> name for a helper class constant,
        /// emitting it the first time it is referenced.
        /// </summary>
        private string EnsureHelperConstant(IFieldSymbol field)
        {
            string key = field.ContainingType.ToDisplayString() + "." + field.Name;
            if (helperConstantNames.TryGetValue(key, out string? existing)) return existing;

            string baseName = field.ContainingType.Name + "_" + field.Name;
            string name = baseName;
            int suffix = 2;
            while (!emittedNames.Add(name))
                name = baseName + "_" + suffix++;

            helperConstantNames[key] = name;

            if (!field.HasConstantValue || field.ConstantValue is null)
                throw new NotSupportedException(
                    $"Helper constant '{field.ContainingType.Name}.{field.Name}' is not a compile-time constant. " +
                    "Only const fields can be referenced from shader code.");

            string hlslType = ConvertType(field.Type);
            constantDeclarations.Add($"static const {hlslType} {name} = {LiteralText(field.ConstantValue, hlslType)};");
            return name;
        }

        private static string HelperKey(IMethodSymbol method)
            => method.ContainingType.ToDisplayString() + "." + method.Name + "(" +
               string.Join(",", method.Parameters.Select(parameter => parameter.Type.ToDisplayString())) + ")";

        private void WriteIndent() => hlsl.Append(' ', indentLvl * 4);
        #endregion HelperFunctions
    }
}

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
    /// <param name="semanticModel">Semantic model used for translation</param>
    /// <param name="shaderType">
    /// Optional runtime type of the shader struct. When supplied, structured-buffer
    /// element structs are resolved and emitted from metadata, so element types do
    /// not have to be declared in the same source file as the shader.
    /// </param>
    public class DivisionTranslator(SemanticModel semanticModel, Type? shaderType = null) : CSharpSyntaxWalker
    {
        // Translation vars
        private readonly SemanticModel semanticModel = semanticModel;
        private readonly Dictionary<string, FieldInfo> shaderFields = BuildShaderFields(shaderType);
        private readonly Dictionary<string, Type> knownTypes = BuildKnownTypes(shaderType);
        private readonly StringBuilder hlsl = new StringBuilder();
        private int indentLvl = 0;
        private int? threadsX, threadsY, threadsZ;

        // Storage for translation
        private readonly List<string> resourceDeclarations = [];
        private readonly List<string> variableDeclarations = [];
        private readonly List<string> structDeclarations = [];
        private readonly List<string> inlineFunctions = [];
        private readonly List<string> kernelNames = [];
        private readonly HashSet<string> processedStructs = [];
        private readonly Dictionary<string, string> bufferLengthHelpers = [];

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

            // Resources and globals must be declared before inline functions that
            // reference them; HLSL requires declarations before use.
            foreach (string resource in resourceDeclarations) hlsl.AppendLine(resource);

            // Emit a default sampler whenever any read-only Texture2D exists.
            // The HLSL Sample/SampleLevel intrinsics require one; we wire it to s0
            // so it doesn't collide with the t/u/b register slots
            if (UsesReadOnlyTexture2D())
                hlsl.AppendLine("SamplerState DivisionDefaultSampler : register(s0);");

            foreach (string helper in bufferLengthHelpers.Values) hlsl.AppendLine(helper);
            foreach (string variable in variableDeclarations) hlsl.AppendLine(variable);
            foreach (string func in inlineFunctions) hlsl.AppendLine(func);

            if (kernelDefinitions.Count > 0) hlsl.AppendLine();

            for (int i = 0; i < kernelDefinitions.Count; i++)
            {
                if (i > 0) hlsl.AppendLine();
                hlsl.Append(kernelDefinitions[i]);
            }
            return hlsl.ToString();
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
        /// Visits statements like "float3 pos = new float3(1,2,3);".
        /// </summary>
        public override void VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            TypeInfo typeInfo = semanticModel.GetTypeInfo(node.Declaration.Type);
            string hlslType = ConvertType(typeInfo.Type);

            var variables = node.Declaration.Variables;
            foreach (VariableDeclaratorSyntax variable in variables) // Works for nested initialization, eg. int a = 1, b = 3, c = 3;
            {
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

            hlsl.Append($"{typeName}("); // Open argument list
            for (int i = 0; i < node.ArgumentList!.Arguments.Count; i++) // Visit each argument
            {
                if (i > 0) hlsl.Append(", ");
                Visit(node.ArgumentList.Arguments[i].Expression);
            }
            hlsl.Append(')'); // Close argument list
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
                    Visit(node.ArgumentList.Arguments[0].Expression);
                    hlsl.Append(", ");
                    if (node.ArgumentList.Arguments.Count > 1)
                        Visit(node.ArgumentList.Arguments[1].Expression);
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
                    Visit(node.ArgumentList.Arguments[i].Expression);
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
                            Visit(node.ArgumentList.Arguments[i].Expression);
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
                hlsl.Append(method.Name);
                hlsl.Append('(');
                for (int i = 0; i < node.ArgumentList.Arguments.Count; i++)
                {
                    if (i > 0) hlsl.Append(", ");
                    Visit(node.ArgumentList.Arguments[i].Expression);
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
                        Visit(node.ArgumentList.Arguments[i].Expression);
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
                Visit(node.ArgumentList.Arguments[i].Expression);
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
        /// Translates literal expressions like output numbers, strings, etc.
        /// </summary>
        public override void VisitLiteralExpression(LiteralExpressionSyntax node)
        {
            string text = node.Token.Text;

            // Remove 'f' suffix from float literals
            if (text.EndsWith('f') || text.EndsWith('F')) text = text.TrimEnd('f', 'F');
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
        /// Translates a non-kernel method to an HLSL inline function.
        /// </summary>
        private void TranslateInlineFunction(MethodDeclarationSyntax method)
        {
            StringBuilder builder = new StringBuilder();
            ITypeSymbol? returnType = semanticModel.GetDeclaredSymbol(method)?.ReturnType;
            string hlslReturnType = ConvertType(returnType);
            List<string> parameters = [];

            // Parameter setup
            foreach (ParameterSyntax param in method.ParameterList.Parameters)
            {
                ITypeSymbol? paramType = semanticModel.GetTypeInfo(param.Type!).Type;
                string hlslParamType = ConvertType(paramType);
                parameters.Add($"{hlslParamType} {param.Identifier.Text}");
            }

            // Function signature
            builder.Append($"{hlslReturnType} {method.Identifier.Text}({string.Join(", ", parameters)})");
            builder.AppendLine();
            builder.AppendLine("{");

            // Temporarily store current HLSL content and switch to new builder for the body
            string originalHlsl = hlsl.ToString();
            hlsl.Clear();
            int oldIndent = indentLvl;
            indentLvl = 1;

            foreach (StatementSyntax statement in method.Body!.Statements) Visit(statement);

            string body = hlsl.ToString();
            builder.Append(body);

            hlsl.Clear(); // Restore original HLSL
            hlsl.Append(originalHlsl);
            indentLvl = oldIndent;

            builder.AppendLine("}");
            inlineFunctions.Add(builder.ToString());
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
                SpecialType.System_UInt32 => "uint",
                SpecialType.System_Boolean => "bool",
                _ => typeSymbol.Name // float3, int2, etc.
            };
        }

        /// <summary>
        /// Gathers all fields in the shader struct.
        /// </summary>
        private void CollectFields(StructDeclarationSyntax structDeclaration)
        {
            foreach (FieldDeclarationSyntax field in structDeclaration.Members.OfType<FieldDeclarationSyntax>())
            {
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
                else
                {
                    // Check if this is a custom struct type that needs translation
                    ITypeSymbol? typeSymbol = semanticModel.GetTypeInfo(fieldType).Type;
                    if (typeSymbol is INamedTypeSymbol namedType && namedType.TypeKind == TypeKind.Struct && !IsBuiltInType(namedType.Name))
                        TranslateStructIfNeeded(namedType);

                    // This is a regular variable (float, int, float3, etc.)
                    string? hlslDeclaration = TranslateVariableField(fieldType, fieldName);
                    if (hlslDeclaration != null) variableDeclarations.Add(hlslDeclaration);
                }
            }
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

        /// <summary>
        /// Writes an indentation to the HLSL string builder.
        /// </summary>
        private void WriteIndent() => hlsl.Append(' ', indentLvl * 4);

        #endregion HelperFunctions
    }
}

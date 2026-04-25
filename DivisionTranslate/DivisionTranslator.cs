using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Diagnostics;
using System.Text;

namespace DivisionTranslate
{
    /// <summary>
    /// Syntax walkder that translates C# source code to HLSL source code based on the ruleset defined for the Division Engine.
    /// </summary>
    /// <param name="semanticModel">Semantic model used for translation</param>
    public class DivisionTranslator(SemanticModel semanticModel) : CSharpSyntaxWalker
    {
        // Translation vars
        private readonly SemanticModel semanticModel = semanticModel;
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

        /// <summary>
        /// Translates C# source code to HLSL source code.
        /// </summary>
        /// <param name="shaderStruct">The struct containing the shader</param>
        /// <param name="kernelMethod">Entering method systax (must have [Kernel] attribute)</param>
        /// <returns>HLSL source code</returns>
        public string Translate(StructDeclarationSyntax shaderStruct)
        {
            List<MethodDeclarationSyntax> kernels = FindAllKernels(shaderStruct);
            List<MethodDeclarationSyntax> inlineFuncs = FindAllInlineFunctions(shaderStruct);
            CollectFields(shaderStruct);

            // Translate inline functions separately
            foreach (MethodDeclarationSyntax func in inlineFuncs) TranslateInlineFunction(func);

            // Note: do not need #pragma kernel directives - this is a unity specific feature

            hlsl.AppendLine();
            foreach (string structDecl in structDeclarations) hlsl.AppendLine(structDecl); // Add struct declarations
            foreach (string func in inlineFunctions) hlsl.AppendLine(func); // Add inline functions

            // Add resource & variable declarations
            foreach (string resource in resourceDeclarations) hlsl.AppendLine(resource);
            if (resourceDeclarations.Count > 0 && variableDeclarations.Count > 0) hlsl.AppendLine();
            foreach (string variable in variableDeclarations) hlsl.AppendLine(variable);
            if ((resourceDeclarations.Count > 0 || variableDeclarations.Count > 0) && kernels.Count > 0) hlsl.AppendLine();
            
            for (int i = 0; i < kernels.Count; i++) // Add each kernel
            {
                if (i > 0) hlsl.AppendLine();
                TranslateKernel(kernels[i]);
            }
            return hlsl.ToString();
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
        /// Translates invocating expressions like "math.length(pos)".
        /// </summary>
        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            if (node.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                string methodName = memberAccess.Name.Identifier.Text;
                ExpressionSyntax instance = memberAccess.Expression;

                // Check if the method belongs to a struct type
                IMethodSymbol? methodSymbol = semanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol;
                if (methodSymbol?.ContainingType?.TypeKind == TypeKind.Struct)
                {
                    // This is a struct method call - translate to function call with instance as first param
                    hlsl.Append($"{methodName}(");
                    Visit(instance); // Pass the instance as first parameter
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

            SymbolInfo symbolInfo = semanticModel.GetSymbolInfo(node);
            if (symbolInfo.Symbol is IMethodSymbol method)
            {
                // Check if math function - output just the name
                if (method.ContainingType?.Name == "math") hlsl.Append(method.Name);
                else hlsl.Append(method.Name);

                hlsl.Append('('); // Open argument list
                for (int i = 0; i < node.ArgumentList.Arguments.Count; i++)
                {
                    if (i > 0) hlsl.Append(", ");
                    Visit(node.ArgumentList.Arguments[i].Expression);
                }
                hlsl.Append(')'); // Close argument list
            }
            else
            {
                // Fallback: try to detect math. pattern from syntax
                if (node.Expression is MemberAccessExpressionSyntax memberAccessFallback &&
                    memberAccessFallback.Expression.ToString() == "math")
                {
                    hlsl.Append(memberAccessFallback.Name.Identifier.Text);
                    hlsl.Append('('); // Open argument list
                    for (int i = 0; i < node.ArgumentList.Arguments.Count; i++)
                    {
                        if (i > 0) hlsl.Append(", ");
                        Visit(node.ArgumentList.Arguments[i].Expression);
                    }
                    hlsl.Append(')'); // Close argument list
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
            Visit(node.Expression);
            hlsl.Append($".{node.Name.Identifier.Text}");
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

                if (isShaderResource) // This is a GPU resource (buffer, texture, etc.)
                {
                    string? hlslDeclaration = TranslateResourceField(fieldType, fieldName);
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
        /// <returns>HLSL resouce field type syntax</returns>
        /// <exception cref="NotSupportedException">If the type is not supported as a shader resource</exception>
        private string? TranslateResourceField(TypeSyntax fieldType, string fieldName)
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

                // Handle the element type
                TypeSyntax typeArg = generic.TypeArgumentList.Arguments[0];
                ITypeSymbol? typeSymbol = semanticModel.GetTypeInfo(typeArg).Type;
                string elementType = ConvertType(typeSymbol);

                // If StructuredBuffer, translate the element struct
                if ((genericName == "StructuredBuffer" || genericName == "RWStructuredBuffer") &&
                    typeSymbol is INamedTypeSymbol namedType && namedType.TypeKind == TypeKind.Struct)
                    TranslateStructIfNeeded(namedType);

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

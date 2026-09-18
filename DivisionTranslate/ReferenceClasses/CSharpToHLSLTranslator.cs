//
// Copyright (c) 2026 Rex Woodfield and DivisionTranslate contributors
//
// This file is part of DivisionTranslate and is subject to the terms
// of the DivisionTranslate License. See the LICENSE.txt file in the
// project root for full license terms.
//

//using Microsoft.CodeAnalysis;
//using Microsoft.CodeAnalysis.CSharp;
//using Microsoft.CodeAnalysis.CSharp.Syntax;
//using SharpGen.Runtime;
//using System.Reflection;
//using System.Runtime.InteropServices;
//using System.Text;
//using Vortice.D3DCompiler;
//using Vortice.Direct3D;

//namespace DivisionTranslate
//{
//    public class CSharpToHLSLTranslator
//    {
//        private readonly Dictionary<string, string> _typeMappings;
//        private readonly Dictionary<string, string> _builtinFunctions;

//        public CSharpToHLSLTranslator()
//        {
//            _typeMappings = new Dictionary<string, string>
//            {
//                ["float"] = "float",
//                ["float2"] = "float2",
//                ["float3"] = "float3",
//                ["float4"] = "float4",
//                ["float4x4"] = "float4x4",
//                ["int"] = "int",
//                ["uint"] = "uint",
//                ["bool"] = "bool",
//                ["uint3"] = "uint3",
//                ["int3"] = "int3",
//                ["SamplerState"] = "SamplerState",
//                ["Texture2D"] = "Texture2D<float4>",
//                ["Texture3D"] = "Texture3D<float>",
//                ["RWTexture2D"] = "RWTexture2D<float4>",
//                ["RWTexture3D"] = "RWTexture3D<float>"
//            };

//            _builtinFunctions = new Dictionary<string, string>
//            {
//                ["abs"] = "abs",
//                ["min"] = "min",
//                ["max"] = "max",
//                ["clamp"] = "clamp",
//                ["saturate"] = "saturate",
//                ["lerp"] = "lerp",
//                ["smoothstep"] = "smoothstep",
//                ["sin"] = "sin",
//                ["cos"] = "cos",
//                ["tan"] = "tan",
//                ["asin"] = "asin",
//                ["acos"] = "acos",
//                ["atan"] = "atan",
//                ["atan2"] = "atan2",
//                ["sqrt"] = "sqrt",
//                ["rsqrt"] = "rsqrt",
//                ["pow"] = "pow",
//                ["exp"] = "exp",
//                ["exp2"] = "exp2",
//                ["log"] = "log",
//                ["log2"] = "log2",
//                ["log10"] = "log10",
//                ["floor"] = "floor",
//                ["ceil"] = "ceil",
//                ["round"] = "round",
//                ["trunc"] = "trunc",
//                ["frac"] = "frac",
//                ["fmod"] = "fmod",
//                ["step"] = "step",
//                ["sign"] = "sign",
//                ["radians"] = "radians",
//                ["degrees"] = "degrees",
//                ["dot"] = "dot",
//                ["cross"] = "cross",
//                ["length"] = "length",
//                ["distance"] = "distance",
//                ["normalize"] = "normalize",
//                ["reflect"] = "reflect",
//                ["refract"] = "refract",
//                ["faceforward"] = "faceforward",
//                ["mul"] = "mul"
//            };
//        }

//        public ShaderCompilationResult TranslateShader(Type shaderClass)
//        {
//            var result = new ShaderCompilationResult();
//            var hlslBuilder = new StringBuilder();

//            try
//            {
//                // Get all fields (constant buffers and resources)
//                var fields = shaderClass.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
//                var constantBuffers = new List<FieldInfo>();
//                var resources = new List<FieldInfo>();
//                var shaderMethods = new List<MethodInfo>();

//                // Categorize fields
//                foreach (var field in fields)
//                {
//                    if (field.GetCustomAttribute<ShaderConstantBufferAttribute>() != null)
//                        constantBuffers.Add(field);
//                    else if (field.GetCustomAttribute<ShaderResourceAttribute>() != null)
//                        resources.Add(field);
//                }

//                // Get shader methods
//                var methods = shaderClass.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
//                foreach (var method in methods)
//                {
//                    if (method.GetCustomAttribute<DivisionShaderAttribute>() != null)
//                        shaderMethods.Add(method);
//                }

//                // Generate HLSL
//                GeneratePreamble(hlslBuilder);
//                GenerateConstantBuffers(hlslBuilder, constantBuffers);
//                GenerateResources(hlslBuilder, resources);

//                // Generate helper methods (non-shader methods)
//                foreach (var method in methods)
//                {
//                    if (method.GetCustomAttribute<DivisionShaderAttribute>() == null &&
//                        !method.IsSpecialName &&
//                        method.DeclaringType == shaderClass)
//                    {
//                        GenerateMethod(hlslBuilder, method);
//                    }
//                }

//                // Generate shader entry points
//                foreach (var method in shaderMethods)
//                {
//                    GenerateShaderMethod(hlslBuilder, method);
//                }

//                result.HLSLCode = hlslBuilder.ToString();
//                result.Success = true;

//                // Compile each shader
//                foreach (var method in shaderMethods)
//                {
//                    var attr = method.GetCustomAttribute<DivisionShaderAttribute>();
//                    var profile = "cs_5_0";
//                    var bytecode = CompileShader(result.HLSLCode, method.Name, profile);

//                    if (bytecode != null)
//                    {
//                        result.ShaderBytecodes[method.Name] = bytecode;
//                    }
//                    else
//                    {
//                        result.Success = false;
//                        result.ErrorMessage = $"Failed to compile shader: {method.Name}";
//                        break;
//                    }
//                }
//            }
//            catch (Exception ex)
//            {
//                result.Success = false;
//                result.ErrorMessage = ex.Message;
//            }

//            return result;
//        }

//        private void GeneratePreamble(StringBuilder sb)
//        {
//            sb.AppendLine("// Auto-generated HLSL from C# compute shader");
//            sb.AppendLine("#pragma pack_matrix(row_major)");
//            sb.AppendLine();
//        }

//        private void GenerateConstantBuffers(StringBuilder sb, List<FieldInfo> constantBuffers)
//        {
//            foreach (var buffer in constantBuffers)
//            {
//                var attr = buffer.GetCustomAttribute<ShaderConstantBufferAttribute>();
//                var bufferType = buffer.FieldType;

//                sb.AppendLine($"cbuffer {buffer.Name} : register(b{attr.Slot})");
//                sb.AppendLine("{");

//                var fields = bufferType.GetFields(BindingFlags.Public | BindingFlags.Instance);
//                foreach (var field in fields)
//                {
//                    var hlslType = MapType(field.FieldType);
//                    sb.AppendLine($"    {hlslType} {field.Name};");
//                }

//                sb.AppendLine("};");
//                sb.AppendLine();
//            }
//        }

//        private void GenerateResources(StringBuilder sb, List<FieldInfo> resources)
//        {
//            foreach (var resource in resources)
//            {
//                var attr = resource.GetCustomAttribute<ShaderResourceAttribute>();
//                var resourceType = MapResourceType(resource.FieldType);
//                sb.AppendLine($"{resourceType} {resource.Name} : register(t{attr.Slot});");
//            }
//            sb.AppendLine();
//        }

//        private void GenerateMethod(StringBuilder sb, MethodInfo method)
//        {
//            var returnType = MapType(method.ReturnType);
//            var parameters = string.Join(", ",
//                method.GetParameters().Select(p => $"{MapType(p.ParameterType)} {p.Name}"));

//            sb.AppendLine($"{returnType} {method.Name}({parameters})");
//            sb.AppendLine("{");

//            // For now, generate a simple pass-through
//            if (returnType != "void")
//            {
//                sb.AppendLine($"    {returnType} result = ({returnType})0;");
//                sb.AppendLine("    return result;");
//            }

//            sb.AppendLine("}");
//            sb.AppendLine();
//        }

//        private void GenerateShaderMethod(StringBuilder sb, MethodInfo method)
//        {
//            var attr = method.GetCustomAttribute<DivisionShaderAttribute>();

//            sb.AppendLine($"// {method.Name}");
//            sb.AppendLine($"[numthreads({attr.ThreadGroupX}, {attr.ThreadGroupY}, {attr.ThreadGroupZ})]");
//            sb.AppendLine($"void {method.Name}(uint3 id : SV_DispatchThreadID)");
//            sb.AppendLine("{");

//            // Generate method body by parsing C# code
//            var body = GenerateMethodBody(method);
//            sb.AppendLine(body);

//            sb.AppendLine("}");
//            sb.AppendLine();
//        }

//        private string GenerateMethodBody(MethodInfo method)
//        {
//            // For now, generate a simple body
//            // In a real implementation, you'd parse the IL or use Roslyn to translate
//            var sb = new StringBuilder();
//            sb.AppendLine("    // TODO: Translate C# method body to HLSL");
//            sb.AppendLine("    // Original method: " + method.Name);
//            sb.AppendLine("    ");
//            sb.AppendLine("    // Example: writing to output texture");
//            sb.AppendLine("    // result[id.xy] = float4(1, 0, 0, 1);");

//            return sb.ToString();
//        }

//        private static byte[]? CompileShader(string hlslCode, string entryPoint, string profile = "cs_5_0", ShaderMacro[]? defines = null, Include? include = default)
//        {
//            try
//            {
//                var result = Compiler.Compile(
//                    hlslCode,
//                    defines,
//                    include,
//                    entryPoint,
//                    "shader.hlsl",
//                    profile,
//                    ShaderFlags.None,
//                    EffectFlags.None,
//                    out Blob bytecode,
//                    out Blob errors
//                );

//                if (result.Failure)
//                {
//                    string errorMessage = "Unknown error";
//                    if (errors != null && errors.BufferSize > 0)
//                    {
//                        unsafe
//                        {
//                            errorMessage = new string((sbyte*)errors.BufferPointer, 0, (int)errors.BufferSize.Value);
//                        }
//                        errors.Dispose();
//                    }
//                    throw new Exception($"HLSL compilation failed: {errorMessage}");
//                }

//                var bytecodeArray = new byte[bytecode.BufferSize];
//                unsafe
//                {
//                    Marshal.Copy(bytecode.BufferPointer, bytecodeArray, 0, (int)bytecode.BufferSize.Value);
//                }

//                bytecode.Dispose();
//                errors?.Dispose();

//                return bytecodeArray;
//            }
//            catch (Exception ex)
//            {
//                Console.WriteLine($"Compilation error: {ex.Message}");
//                return null;
//            }
//        }

//        private string MapType(Type type)
//        {
//            var typeName = type.Name;
//            if (type.IsGenericType)
//            {
//                typeName = type.GetGenericTypeDefinition().Name;
//            }

//            return _typeMappings.TryGetValue(typeName, out var mapped) ? mapped : typeName;
//        }

//        private string MapResourceType(Type type)
//        {
//            var typeName = type.Name;

//            if (type.IsGenericType)
//            {
//                var genericArg = type.GetGenericArguments()[0];
//                var elementType = MapType(genericArg);
//                var baseType = type.GetGenericTypeDefinition().Name;

//                return baseType switch
//                {
//                    "RWTexture2D`1" => $"RWTexture2D<{elementType}>",
//                    "RWTexture3D`1" => $"RWTexture3D<{elementType}>",
//                    "Texture2D`1" => $"Texture2D<{elementType}>",
//                    "Texture3D`1" => $"Texture3D<{elementType}>",
//                    _ => baseType
//                };
//            }

//            return typeName switch
//            {
//                "SamplerState" => "SamplerState",
//                "Texture2D" => "Texture2D<float4>",
//                "Texture3D" => "Texture3D<float>",
//                "RWTexture2D" => "RWTexture2D<float4>",
//                "RWTexture3D" => "RWTexture3D<float>",
//                _ => typeName
//            };
//        }
//    }
//}

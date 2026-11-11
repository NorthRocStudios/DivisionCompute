//
// Copyright (c) 2026 Rex Woodfield and DivisionCompute contributors
//
// This file is part of DivisionCompute and is subject to the terms
// of the DivisionCompute License. See the LICENSE.txt file in the
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

namespace DivisionEngine
{
    /// <summary>
    /// Translates C# shader structs to HLSL and compiles them to DXBC bytecode
    /// that can be loaded into a D3D11 compute shader.
    /// </summary>
    /// <remarks>
    /// A single instance can be reused across many shader types and threads. The
    /// Roslyn metadata references are built per shader so element structs declared
    /// in other files or assemblies can be resolved.
    /// </remarks>
    public class DivisionShaderCompiler
    {
        /// <summary>
        /// Raised for every translation, cache lookup and D3DCompile attempt. Hosts
        /// forward these into their own log (the editor console, for example) so shader
        /// compilation is visible next to the rest of the engine's logging.
        /// </summary>
        /// <remarks>
        /// May be raised from a background thread: hosts that touch UI must marshal.
        /// </remarks>
        public static event Action<ShaderLogLevel, string>? Log;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> compiledShaders = new();

        /// <summary>
        /// True when the named shader has already been compiled successfully in this
        /// process. Background preloading uses this to skip shaders the renderer has
        /// compiled synchronously, which avoids translating them twice.
        /// </summary>
        public static bool IsShaderCompiled(string shaderTypeName) => compiledShaders.ContainsKey(shaderTypeName);

        private static void LogMessage(ShaderLogLevel level, string message)
        {
            try
            {
                Log?.Invoke(level, message);
            }
            catch
            {
                // A logging subscriber must never break shader compilation.
            }
        }

        /// <summary>
        /// Creates a new compiler. Metadata references are resolved per shader.
        /// </summary>
        public DivisionShaderCompiler()
        {
        }

        /// <summary>
        /// One-shot helper: translates a shader type to HLSL and compiles it to DXBC.
        /// </summary>
        /// <param name="shaderType">The C# struct marked with <see cref="ShaderAttribute"/>.</param>
        /// <returns>
        /// A <see cref="HLSLCompilationResult"/> containing either the compiled bytecode
        /// or the diagnostic message from whichever stage failed.
        /// </returns>
        public HLSLCompilationResult CompileShader(Type shaderType)
        {
            // Translate C# to HLSL
            HLSLTranslationResult translationResult = TranslateShader(shaderType);
            if (!translationResult.IsSuccess)
                return HLSLCompilationResult.Failure(translationResult.ErrorMessage ?? "No error message provided");

            // Compile HLSL to bytecode (all kernels in one shader)
            HLSLCompilationResult compileResult = CompileHLSL(translationResult);
            return compileResult;
        }

        /// <summary>
        /// Compiles the HLSL source of a successful translation into DXBC bytecode.
        /// </summary>
        /// <param name="translatedShader">A successful result from <see cref="TranslateShader"/>.</param>
        /// <returns>
        /// A result containing the compiled bytecode on success, or the D3DCompiler
        /// error output on failure.
        /// </returns>
        public static HLSLCompilationResult CompileHLSL(HLSLTranslationResult translatedShader)
            => CompileHLSL(translatedShader, ShaderFlags.None);

        /// <summary>
        /// Compiles the HLSL source of a successful translation with explicit
        /// D3DCompile flags.
        /// </summary>
        /// <param name="translatedShader">A successful result from <see cref="TranslateShader"/>.</param>
        /// <param name="flags">Flags passed to D3DCompile.</param>
        /// <returns>
        /// A result containing the compiled bytecode on success, or the D3DCompiler
        /// error output on failure.
        /// </returns>
        public static HLSLCompilationResult CompileHLSL(HLSLTranslationResult translatedShader, ShaderFlags flags)
        {
            ArgumentNullException.ThrowIfNull(translatedShader);

            if (!translatedShader.IsSuccess || string.IsNullOrWhiteSpace(translatedShader.HLSLCode))
                return HLSLCompilationResult.Failure(
                    translatedShader.ErrorMessage ?? "No HLSL source was produced.",
                    translatedShader.ShaderTypeName);

            if (translatedShader.KernelNames.Count == 0)
                return HLSLCompilationResult.Failure(
                    $"Shader '{translatedShader.ShaderTypeName}' has no kernels to compile.",
                    translatedShader.ShaderTypeName);

            // D3DCompile compiles one entry point per call. Compile every kernel
            // separately so dispatching a non-first kernel does not silently run
            // the first kernel's bytecode.
            var kernelBytecodes = new List<byte[]>(translatedShader.KernelNames.Count);
            var debugOutput = new StringBuilder();

            foreach (string kernelName in translatedShader.KernelNames)
            {
                // D3DCompile is extremely slow for the large raymarching shader (its
                // front end expands the inlined call graph to six-figure instruction
                // counts), so a successful compilation is cached on disk and reused
                // until the HLSL or the flags change.
                byte[]? cachedBytecode = TryReadCachedBytecode(translatedShader, kernelName, flags);
                if (cachedBytecode is not null)
                {
                    kernelBytecodes.Add(cachedBytecode);
                    debugOutput.AppendLine($"Kernel '{kernelName}': {cachedBytecode.Length} bytes (cache hit)");
                    compiledShaders[translatedShader.ShaderTypeName] = 0;
                    LogMessage(ShaderLogLevel.Info,
                        $"Shader '{translatedShader.ShaderTypeName}' kernel '{kernelName}': cache hit ({cachedBytecode.Length:N0} bytes)");
                    continue;
                }

                LogMessage(ShaderLogLevel.Info,
                    $"Shader '{translatedShader.ShaderTypeName}' kernel '{kernelName}': compiling HLSL ({flags})...");

                Stopwatch compileWatch = Stopwatch.StartNew();
                Result compileResult = Compiler.Compile(
                    translatedShader.HLSLCode,
                    null!,
                    null!,
                    kernelName,
                    translatedShader.ShaderTypeName,
                    "cs_5_0",
                    flags,
                    out Blob? codeBlob,
                    out Blob? errorBlob);

                long compileMilliseconds = compileWatch.ElapsedMilliseconds;

                string kernelDebug = string.Empty;
                if (errorBlob is not null && errorBlob.AsBytes().Length > 0)
                {
                    kernelDebug = Encoding.UTF8.GetString(errorBlob.AsBytes())
                        ?? compileResult.Description
                        ?? "No additional compilation info";
                }

                if (compileResult.Failure || codeBlob is null)
                {
                    LogMessage(ShaderLogLevel.Error,
                        $"Shader '{translatedShader.ShaderTypeName}' kernel '{kernelName}': compilation failed: {kernelDebug}");
                    if (string.IsNullOrWhiteSpace(kernelDebug))
                        kernelDebug = compileResult.Description ?? "No additional compilation info";

                    Debug.WriteLine("----------------------------------------");
                    Debug.WriteLine($"Division Shader Compiler:\nFailed to compile \"{translatedShader.ShaderTypeName}\" kernel \"{kernelName}\"\n\n{kernelDebug}");
                    Debug.WriteLine("----------------------------------------");

                    errorBlob?.Dispose();
                    codeBlob?.Dispose();
                    return HLSLCompilationResult.Failure(
                        $"Kernel '{kernelName}' failed to compile:\n{kernelDebug}",
                        translatedShader.ShaderTypeName);
                }

                byte[] bytecode = codeBlob.AsBytes();
                kernelBytecodes.Add(bytecode);
                compiledShaders[translatedShader.ShaderTypeName] = 0;
                WriteCachedBytecode(translatedShader, kernelName, flags, bytecode);
                debugOutput.AppendLine($"Kernel '{kernelName}': {bytecode.Length} bytes");
                LogMessage(ShaderLogLevel.Info,
                    $"Shader '{translatedShader.ShaderTypeName}' kernel '{kernelName}': compiled in {compileMilliseconds:N0} ms ({bytecode.Length:N0} bytes)");
                if (!string.IsNullOrWhiteSpace(kernelDebug))
                    LogMessage(ShaderLogLevel.Warning,
                        $"Shader '{translatedShader.ShaderTypeName}' kernel '{kernelName}' compiler output: {kernelDebug.Trim()}");
                if (!string.IsNullOrWhiteSpace(kernelDebug))
                    debugOutput.AppendLine(kernelDebug.TrimEnd());

                codeBlob.Dispose();
                errorBlob?.Dispose();
            }

            Debug.WriteLine("----------------------------------------");
            Debug.WriteLine($"Division Shader Compiler:\nCompiled \"{translatedShader.ShaderTypeName}\" ({kernelBytecodes.Count} kernel(s))");
            Debug.WriteLine(debugOutput.ToString().TrimEnd());
            Debug.WriteLine("----------------------------------------");

            return HLSLCompilationResult.Success(
                translatedShader.HLSLCode,
                translatedShader.KernelNames,
                debugOutput.ToString().TrimEnd(),
                translatedShader.ShaderTypeName,
                kernelBytecodes);

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

        /// <summary>
        /// Parses the C# source file containing <paramref name="shaderType"/> and emits HLSL.
        /// </summary>
        /// <param name="shaderType">A struct decorated with <see cref="ShaderAttribute"/>.</param>
        /// <returns>Either the translated HLSL or a diagnostic message describing the failure.</returns>
        public HLSLTranslationResult TranslateShader(Type shaderType)
        {
            ArgumentNullException.ThrowIfNull(shaderType);

            Stopwatch translationWatch = Stopwatch.StartNew();
            try
            {
                string? sourcePath = GetSourceFilePath(shaderType);
                if (sourcePath is null)
                {
                    string attrPath = shaderType.GetCustomAttribute<ShaderAttribute>()?.SourceFilePath
                        ?? "(no [Shader] attribute)";
                    string message =
                        $"Could not locate the source file for shader '{shaderType.Name}'. " +
                        $"Attribute path was \"{attrPath}\". " +
                        $"Hint: use [Shader] with no arguments to let the compiler inject " +
                        $"the correct path via [CallerFilePath].";
                    LogMessage(ShaderLogLevel.Error, $"Shader '{shaderType.Name}': {message}");
                    return HLSLTranslationResult.Failure(message, shaderType.Name);
                }

                SyntaxTree syntaxTree = GetSyntaxTree(sourcePath);

                // Shared helper classes (ShaderMath, PBR, SDFPrimitives, ...) must be
                // present as source, not metadata, so their method bodies can be
                // translated when a shader calls them.
                CSharpCompilation compilation = CSharpCompilation.Create($"{shaderType.Name}_Compilation")
                    .AddSyntaxTrees(GetProjectSyntaxTrees(sourcePath, syntaxTree).Prepend(syntaxTree))
                    .AddReferences(BuildMetadataReferences(shaderType));

                SemanticModel semanticModel = compilation.GetSemanticModel(syntaxTree);
                SyntaxNode root = syntaxTree.GetRoot();
                StructDeclarationSyntax? structDeclaration = FindStructDeclaration(root, shaderType.Name);
                if (structDeclaration == null)
                {
                    string message = $"Struct '{shaderType.Name}' not found in {sourcePath}";
                    LogMessage(ShaderLogLevel.Error, $"Shader '{shaderType.Name}': {message}");
                    return HLSLTranslationResult.Failure(message, shaderType.Name);
                }

                List<string> kernelNames = FindAllKernelNames(structDeclaration, semanticModel);
                if (kernelNames.Count == 0)
                {
                    string message = $"No [Kernel] methods found in struct '{shaderType.Name}'";
                    LogMessage(ShaderLogLevel.Error, $"Shader '{shaderType.Name}': {message}");
                    return HLSLTranslationResult.Failure(message, shaderType.Name);
                }

                DivisionTranslator translator = new DivisionTranslator(semanticModel, shaderType, compilation);
                string hlslCode = translator.Translate(structDeclaration);
                LogMessage(ShaderLogLevel.Info,
                    $"Shader '{shaderType.Name}': translated {hlslCode.Count(c => c == '\n') + 1:N0} HLSL lines in {translationWatch.ElapsedMilliseconds:N0} ms");
                return HLSLTranslationResult.Success(hlslCode, kernelNames, shaderType.Name);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Division Shader Compiler: translation of '{shaderType.Name}' failed:\n{ex}");
                LogMessage(ShaderLogLevel.Error,
                    $"Shader '{shaderType.Name}': translation failed: {ex.GetType().Name}: {ex.Message}");
                return HLSLTranslationResult.Failure(
                    $"{ex.GetType().Name}: {ex.Message}",
                    shaderType.Name);
            }
        }

        /// <summary>
        /// Builds a Roslyn reference set that can resolve the shader type, its
        /// field types, and element structs declared in other files or assemblies.
        /// </summary>
        private static List<MetadataReference> BuildMetadataReferences(Type shaderType)
        {
            // Framework facades first: DivisionMath's public surface mentions
            // System.Runtime types (math.dot returns System.Single), and Roslyn cannot
            // bind those members without the facade reference, which silently turns
            // math.* calls into overload-resolution failures.
            var references = new List<MetadataReference>(FrameworkReferences.Value);
            var referencePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visitedAssemblies = new HashSet<Assembly>();
            var visitedTypes = new HashSet<Type>();

            AddAssembly(typeof(object).Assembly);
            AddAssembly(typeof(float3).Assembly);
            AddAssembly(typeof(ShaderAttribute).Assembly);
            AddAssembly(shaderType.Assembly);

            foreach (FieldInfo field in shaderType.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                AddType(field.FieldType);
            }

            return references;

            void AddAssembly(Assembly assembly)
            {
                if (assembly.IsDynamic || !visitedAssemblies.Add(assembly))
                    return;

                string? location = assembly.Location;
                if (string.IsNullOrEmpty(location) || !File.Exists(location))
                    return;

                if (!referencePaths.Add(location))
                    return;

                references.Add(MetadataReference.CreateFromFile(location));
            }

            void AddType(Type type)
            {
                if (!visitedTypes.Add(type))
                    return;

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

                if (type.IsPointer || type.IsByRef)
                    return;

                AddAssembly(type.Assembly);

                if (type.IsPrimitive || type.IsEnum || type == typeof(string) || ShaderStructLayout.IsBuiltIn(type))
                    return;

                if (!type.IsValueType)
                    return;

                foreach (FieldInfo field in type.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    AddType(field.FieldType);
                }
            }
        }

        /// <summary>
        /// The per-user directory used for compiled shader bytecode when
        /// <see cref="CacheDirectory"/> is not overridden.
        /// </summary>
        public static string DefaultCacheDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DivisionEngine",
            "ShaderCache");

        /// <summary>
        /// Directory holding compiled shader bytecode. Defaults to
        /// <see cref="DefaultCacheDirectory"/>; set to null (or define
        /// DIVISION_DISABLE_SHADER_CACHE=1) to always recompile.
        /// </summary>
        public static string? CacheDirectory { get; set; } = DefaultCacheDirectory;

        private static bool CacheDisabled =>
            CacheDirectory is null ||
            Environment.GetEnvironmentVariable("DIVISION_DISABLE_SHADER_CACHE") is "1" or "true";

        private static readonly byte[] CacheHeader = "DVSHC1\0"u8.ToArray();

        private static string? CachePath(HLSLTranslationResult shader, string kernelName, ShaderFlags flags)
        {
            if (CacheDisabled) return null;

            string keySource = string.Concat(
                typeof(DivisionShaderCompiler).Assembly.GetName().Version?.ToString() ?? "0",
                "|", shader.ShaderTypeName, "|", kernelName, "|", flags.ToString(),
                "|", shader.HLSLCode ?? string.Empty);

            byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(keySource));
            string name = Convert.ToHexString(hash, 0, 16).ToLowerInvariant() + ".dxbc";
            return Path.Combine(CacheDirectory!, name);
        }

        private static byte[]? TryReadCachedBytecode(HLSLTranslationResult shader, string kernelName, ShaderFlags flags)
        {
            string? path = CachePath(shader, kernelName, flags);
            if (path is null || !File.Exists(path)) return null;
            return ReadCacheFile(path);
        }

        private static byte[]? ReadCacheFile(string path)
        {
            try
            {
                byte[] content = File.ReadAllBytes(path);
                if (content.Length <= CacheHeader.Length) return null;

                for (int i = 0; i < CacheHeader.Length; i++)
                    if (content[i] != CacheHeader[i]) return null;

                return content[CacheHeader.Length..];
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static void WriteCachedBytecode(HLSLTranslationResult shader, string kernelName, ShaderFlags flags, byte[] bytecode)
        {
            string? path = CachePath(shader, kernelName, flags);
            if (path is null) return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (FileStream stream = File.Create(temporary))
                {
                    stream.Write(CacheHeader);
                    stream.Write(bytecode);
                }
                File.Move(temporary, path, overwrite: true);
            }
            catch (IOException)
            {
                // A cache miss is always safe; a failed write must not fail the shader.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>
        /// Metadata references for the runtime and framework assemblies, resolved once
        /// from the trusted platform assembly list so every shader compilation can bind
        /// framework types that DivisionMath and the engine surface in their signatures.
        /// </summary>
        private static readonly Lazy<List<MetadataReference>> FrameworkReferences = new(CreateFrameworkReferences);

        private static List<MetadataReference> CreateFrameworkReferences()
        {
            var references = new List<MetadataReference>();

            // The facades that carry the framework types DivisionMath and the engine
            // mention in their signatures. Referencing the whole TPA list works too but
            // multiplies the cost of every Roslyn semantic-model query, which dominates
            // translation time for a large shader.
            string[] frameworkAssemblies =
            [
                "System.Runtime.dll",
                "System.Runtime.Extensions.dll",
                "System.Collections.dll",
                "System.Memory.dll",
                "System.Runtime.InteropServices.dll",
                "System.Threading.dll",
                "System.Threading.Tasks.dll",
                "System.Numerics.Vectors.dll",
                "netstandard.dll",
            ];

            if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trustedAssemblies)
            {
                foreach (string path in trustedAssemblies.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                    string fileName = Path.GetFileName(path);
                    if (!frameworkAssemblies.Contains(fileName, StringComparer.OrdinalIgnoreCase)) continue;

                    try
                    {
                        references.Add(MetadataReference.CreateFromFile(path));
                    }
                    catch (IOException)
                    {
                        // A locked assembly simply cannot be referenced; the shader will
                        // report the resulting missing type if it actually needed it.
                    }
                }
            }

            if (references.Count == 0)
            {
                string? corelib = typeof(object).Assembly.Location;
                if (!string.IsNullOrEmpty(corelib) && File.Exists(corelib))
                    references.Add(MetadataReference.CreateFromFile(corelib));
            }

            return references;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SyntaxTree> SourceTreeCache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Parses a source file, reusing the tree while the file is unchanged. Shaders
        /// are translated once at startup, but the editor recompiles on script changes,
        /// so the cache key includes the write time.
        /// </summary>
        private static SyntaxTree GetSyntaxTree(string path)
        {
            string key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
            return SourceTreeCache.GetOrAdd(key, _ => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path));
        }

        /// <summary>
        /// Returns the syntax trees of every C# file in the shader's project, excluding
        /// the shader's own file. Helper classes live next to the shaders, and their
        /// bodies can only be translated when they are part of the same compilation.
        /// </summary>
        private static IEnumerable<SyntaxTree> GetProjectSyntaxTrees(string shaderSourcePath, SyntaxTree shaderTree)
        {
            string? root = Path.GetDirectoryName(Path.GetFullPath(shaderSourcePath));
            while (root is not null && !Directory.EnumerateFiles(root, "*.csproj").Any())
                root = Path.GetDirectoryName(root);

            if (root is null) yield break;

            string shaderFullPath = Path.GetFullPath(shaderSourcePath);
            foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
                    file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                    continue;

                if (string.Equals(Path.GetFullPath(file), shaderFullPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                yield return GetSyntaxTree(file);
            }
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

        /// <summary>
        /// Locates the C# source file for a shader type.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Resolution order:
        /// <list type="number">
        ///   <item>Absolute path from the <see cref="ShaderAttribute"/> that still exists on disk.</item>
        ///   <item>Relative path resolved against each candidate base directory.</item>
        ///   <item>Filename-only fallback: try <c>&lt;base&gt;/&lt;filename&gt;</c> and
        ///         <c>&lt;base&gt;/Shaders/&lt;filename&gt;</c> for each base.</item>
        /// </list>
        /// The filename fallback is what makes a project portable: if a developer
        /// copies a solution to another machine and the absolute path from
        /// <c>[CallerFilePath]</c> no longer exists, we still find the shader by name.
        /// </para>
        /// </remarks>
        /// <returns>The absolute path to the source file, or <c>null</c> if not found.</returns>
        private static string? GetSourceFilePath(Type shaderType)
        {
            ShaderAttribute? attr = shaderType.GetCustomAttribute<ShaderAttribute>();
            if (attr is null || string.IsNullOrEmpty(attr.SourceFilePath))
                return null;

            string rawPath = attr.SourceFilePath;

            // Fast path: absolute path that still exists on disk.
            if (Path.IsPathRooted(rawPath) && File.Exists(rawPath))
                return rawPath;

            // Slow path: search by filename across the candidate base directories.
            string fileName = Path.GetFileName(rawPath);
            string? relativeDir = Path.IsPathRooted(rawPath)
                ? null
                : Path.GetDirectoryName(rawPath); // "" if the attribute path had no folder

            foreach (string baseDir in CandidateBaseDirectories(shaderType))
            {
                // Try <baseDir>/<relativeDir>/<fileName> if the attribute had a subfolder
                if (!string.IsNullOrEmpty(relativeDir))
                {
                    string withSubdir = Path.Combine(baseDir, relativeDir, fileName);
                    if (File.Exists(withSubdir)) return withSubdir;
                }

                // Try <baseDir>/<fileName>
                string flat = Path.Combine(baseDir, fileName);
                if (File.Exists(flat)) return flat;

                // Try <baseDir>/Shaders/<fileName> - convention-based fallback
                string underShaders = Path.Combine(baseDir, "Shaders", fileName);
                if (File.Exists(underShaders)) return underShaders;
            }

            return null;
        }

        /// <summary>
        /// Yields the directories we search when resolving a relative shader path,
        /// in order of preference.
        /// </summary>
        private static IEnumerable<string> CandidateBaseDirectories(Type shaderType)
        {
            // Where the running app was launched from (bin/Debug/net10.0/).
            yield return AppContext.BaseDirectory;

            // The directory of the assembly that contains the shader type.
            string? asmDir = Path.GetDirectoryName(shaderType.Assembly.Location);
            if (asmDir is not null)
            {
                yield return asmDir;

                // Walk up from the assembly dir looking for a .csproj or .sln so
                // relative paths like "Shaders/MyShader.cs" resolve against the
                // project root instead of bin/Debug/net10.0/.
                DirectoryInfo? dir = new DirectoryInfo(asmDir);
                while (dir is not null)
                {
                    if (dir.EnumerateFiles("*.csproj").Any() || dir.EnumerateFiles("*.sln").Any())
                    {
                        yield return dir.FullName;
                        break;
                    }
                    dir = dir.Parent;
                }
            }

            // Current working directory - useful when running from project root
            // via `dotnet run` in a dev shell.
            yield return Directory.GetCurrentDirectory();
        }
    }
}

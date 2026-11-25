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
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
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
        /// When <c>true</c> (the default), shaders with a descriptor baked in by the
        /// DivisionCompute source generator are translated by returning that HLSL
        /// instead of starting Roslyn, and their resource and uniform layouts come
        /// from the descriptor too. Set to <c>false</c> to force both translation and
        /// layout discovery to happen at runtime, which is what hot reload needs while
        /// shader sources change.
        /// </summary>
        public static bool PreferGeneratedDescriptors { get; set; } =
            Environment.GetEnvironmentVariable("DIVISION_FORCE_RUNTIME_TRANSLATION") is not ("1" or "true");

        /// <summary>
        /// When <c>true</c> (the default), a generated descriptor is ignored if the
        /// shader's source file changed after the build, and the runtime translator is
        /// used instead.
        /// </summary>
        /// <remarks>
        /// This is what makes "edit shader source, reload shaders" work without a
        /// rebuild: the descriptor records a hash of the source at build time, and a
        /// mismatch sends the shader down the Roslyn path. Deployed builds without
        /// source files keep using the descriptor. Disable with
        /// <c>DIVISION_DISABLE_SOURCE_CHANGE_DETECTION=1</c>.
        /// </remarks>
        public static bool DetectShaderSourceChanges { get; set; } =
            Environment.GetEnvironmentVariable("DIVISION_DISABLE_SOURCE_CHANGE_DETECTION") is not ("1" or "true");

        /// <summary>
        /// When <c>true</c> (the default), every shader translated from the same
        /// project shares one Roslyn compilation and one metadata reference set.
        /// </summary>
        /// <remarks>
        /// Sharing is what makes the steady state fast: parsing, reference import,
        /// and binding happen once per project instead of once per shader. Set to
        /// <c>false</c> to restore the previous per-shader compilation behaviour,
        /// which is useful when diagnosing a stale-cache problem (and is what the
        /// benchmark measures as the "before" number).
        /// </remarks>
        public static bool UseSharedCompilation { get; set; } =
            Environment.GetEnvironmentVariable("DIVISION_DISABLE_SHARED_COMPILATION") is not ("1" or "true");

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
        /// Translates several shaders in parallel, sharing one Roslyn compilation
        /// and one metadata reference set for the whole batch.
        /// </summary>
        /// <param name="shaderTypes">The <c>[Shader]</c> struct types to translate.</param>
        /// <returns>One result per input type, in input order.</returns>
        /// <remarks>
        /// Translation is CPU-bound Roslyn work, so a batch of independent shaders is
        /// the case where parallelising pays off: the shared compilation is built once
        /// and every shader then binds against it concurrently. Shaders that have a
        /// build-time descriptor return immediately (they involve no Roslyn work).
        /// </remarks>
        public IReadOnlyList<HLSLTranslationResult> TranslateShaders(IEnumerable<Type> shaderTypes)
        {
            ArgumentNullException.ThrowIfNull(shaderTypes);

            Type[] types = shaderTypes as Type[] ?? [.. shaderTypes];
            var results = new HLSLTranslationResult[types.Length];
            if (types.Length == 0) return results;

            // Build the shared project state once. Doing it inside Parallel.For would
            // serialise every worker on the first translation anyway, and this also
            // makes the cold path use a single parse instead of a race.
            if (PreferGeneratedDescriptors)
            {
                foreach (Type shaderType in types)
                {
                    if (!DivisionShaderDescriptors.TryGet(shaderType, out _))
                    {
                        WarmProject(shaderType);
                        break;
                    }
                }
            }

            Parallel.For(0, types.Length, index => results[index] = TranslateShader(types[index]));
            return results;
        }

        /// <summary>
        /// Translates and compiles several shaders in parallel.
        /// </summary>
        /// <param name="shaderTypes">The <c>[Shader]</c> struct types to compile.</param>
        /// <returns>One result per input type, in input order.</returns>
        public IReadOnlyList<HLSLCompilationResult> CompileShaders(IEnumerable<Type> shaderTypes)
        {
            IReadOnlyList<HLSLTranslationResult> translations = TranslateShaders(shaderTypes);
            var results = new HLSLCompilationResult[translations.Count];

            Parallel.For(0, translations.Count, index => results[index] = CompileHLSL(translations[index]));
            return results;
        }

        /// <summary>
        /// Builds the shared project compilation (and its semantic models) without
        /// translating anything, so the first parallel translation does not do it.
        /// </summary>
        private static void WarmProject(Type shaderType)
        {
            if (DivisionShaderDescriptors.TryGet(shaderType, out DivisionShaderDescriptor? descriptor) &&
                !IsGeneratedShaderStale(shaderType, descriptor))
            {
                return;
            }

            string? sourcePath = GetSourceFilePath(shaderType);
            if (sourcePath is null)
                return;

            try
            {
                ProjectSourceSet sources = ProjectSourceSet.ForSourceFile(sourcePath);
                sources.GetCompilation(shaderType.Assembly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Warming is best-effort; the real translation reports the failure.
            }
        }

        private static readonly ConcurrentDictionary<string, (long Ticks, long Length, string Hash)> SourceHashCache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// True when the shader's source file no longer matches the hash baked into its
        /// descriptor, which means the source was edited after the build (hot reload).
        /// </summary>
        /// <remarks>
        /// A missing source file (a deployed build) or a descriptor without source
        /// metadata leaves the descriptor authoritative. Hashing is cached by the
        /// file's write time and length, so a process pays one read per shader.
        /// </remarks>
        private static bool IsGeneratedShaderStale(Type shaderType, DivisionShaderDescriptor descriptor)
        {
            if (!DetectShaderSourceChanges)
                return false;

            string? path = descriptor.SourceFilePath;
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(descriptor.SourceHash))
                return false;

            string hash;

            try
            {
                var file = new FileInfo(path);
                if (!file.Exists)
                    return false;

                if (SourceHashCache.TryGetValue(path, out (long Ticks, long Length, string Hash) cached) &&
                    cached.Ticks == file.LastWriteTimeUtc.Ticks &&
                    cached.Length == file.Length)
                {
                    hash = cached.Hash;
                }
                else
                {
                    // Normalize text exactly like the generator does (line endings and
                    // BOM), so a checkout that only changes CRLF/LF is not an "edit".
                    string text = File.ReadAllText(path)
                        .Replace("\r\n", "\n")
                        .Replace('\r', '\n');

                    hash = Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));
                    SourceHashCache[path] = (file.LastWriteTimeUtc.Ticks, file.Length, hash);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return false;
            }

            if (string.Equals(hash, descriptor.SourceHash, StringComparison.OrdinalIgnoreCase))
                return false;

            LogMessage(ShaderLogLevel.Warning,
                $"Shader '{shaderType.Name}': source file changed since the build; using runtime translation ({path})");
            return true;
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
            // the first kernel's bytecode - but do it in parallel: D3DCompile is
            // thread-safe, and a multi-kernel shader otherwise pays the full
            // front-end cost once per kernel, serially.
            int kernelCount = translatedShader.KernelNames.Count;
            var kernelBytecodes = new byte[kernelCount][];
            var kernelDebugOutput = new string?[kernelCount];
            var kernelFailures = new string?[kernelCount];

            Parallel.For(0, kernelCount, index =>
            {
                string kernelName = translatedShader.KernelNames[index];

                // D3DCompile is extremely slow for the large raymarching shader (its
                // front end expands the inlined call graph to six-figure instruction
                // counts), so a successful compilation is cached on disk and reused
                // until the HLSL or the flags change.
                byte[]? cachedBytecode = TryReadCachedBytecode(translatedShader, kernelName, flags);
                if (cachedBytecode is not null)
                {
                    kernelBytecodes[index] = cachedBytecode;
                    kernelDebugOutput[index] = $"Kernel '{kernelName}': {cachedBytecode.Length} bytes (cache hit)";
                    compiledShaders[translatedShader.ShaderTypeName] = 0;
                    LogMessage(ShaderLogLevel.Info,
                        $"Shader '{translatedShader.ShaderTypeName}' kernel '{kernelName}': cache hit ({cachedBytecode.Length:N0} bytes)");
                    return;
                }

                LogMessage(ShaderLogLevel.Info,
                    $"Shader '{translatedShader.ShaderTypeName}' kernel '{kernelName}': compiling HLSL ({flags})...");

                Stopwatch compileWatch = Stopwatch.StartNew();

                try
                {
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

                        kernelFailures[index] = $"Kernel '{kernelName}' failed to compile:\n{kernelDebug}";
                        errorBlob?.Dispose();
                        codeBlob?.Dispose();
                        return;
                    }

                    byte[] bytecode = codeBlob.AsBytes();
                    kernelBytecodes[index] = bytecode;
                    compiledShaders[translatedShader.ShaderTypeName] = 0;
                    WriteCachedBytecode(translatedShader, kernelName, flags, bytecode);

                    var kernelLog = new StringBuilder();
                    kernelLog.AppendLine($"Kernel '{kernelName}': {bytecode.Length} bytes");
                    if (!string.IsNullOrWhiteSpace(kernelDebug))
                        kernelLog.AppendLine(kernelDebug.TrimEnd());
                    kernelDebugOutput[index] = kernelLog.ToString().TrimEnd();

                    LogMessage(ShaderLogLevel.Info,
                        $"Shader '{translatedShader.ShaderTypeName}' kernel '{kernelName}': compiled in {compileMilliseconds:N0} ms ({bytecode.Length:N0} bytes)");
                    if (!string.IsNullOrWhiteSpace(kernelDebug))
                        LogMessage(ShaderLogLevel.Warning,
                            $"Shader '{translatedShader.ShaderTypeName}' kernel '{kernelName}' compiler output: {kernelDebug.Trim()}");

                    codeBlob.Dispose();
                    errorBlob?.Dispose();
                }
                catch (Exception ex)
                {
                    kernelFailures[index] = $"Kernel '{kernelName}' failed to compile: {ex.GetType().Name}: {ex.Message}";
                }
            });

            // Report the first failing kernel in declaration order, so the message is
            // deterministic even though the compilations ran concurrently.
            for (int i = 0; i < kernelCount; i++)
            {
                if (kernelFailures[i] is { } failure)
                    return HLSLCompilationResult.Failure(failure, translatedShader.ShaderTypeName);
            }

            var debugOutput = new StringBuilder();
            foreach (string? kernelOutput in kernelDebugOutput)
            {
                if (!string.IsNullOrEmpty(kernelOutput))
                    debugOutput.AppendLine(kernelOutput);
            }

            var bytecodes = new List<byte[]>(kernelCount);
            for (int i = 0; i < kernelCount; i++)
            {
                if (kernelBytecodes[i] is not { } bytecode)
                    return HLSLCompilationResult.Failure(
                        $"Kernel '{translatedShader.KernelNames[i]}' produced no bytecode.",
                        translatedShader.ShaderTypeName);

                bytecodes.Add(bytecode);
            }

            Debug.WriteLine("----------------------------------------");
            Debug.WriteLine($"Division Shader Compiler:\nCompiled \"{translatedShader.ShaderTypeName}\" ({bytecodes.Count} kernel(s))");
            Debug.WriteLine(debugOutput.ToString().TrimEnd());
            Debug.WriteLine("----------------------------------------");

            return HLSLCompilationResult.Success(
                translatedShader.HLSLCode,
                translatedShader.KernelNames,
                debugOutput.ToString().TrimEnd(),
                translatedShader.ShaderTypeName,
                bytecodes,
                translatedShader.UsedGeneratedDescriptor);

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

            // Build-time path: the source generator already translated this shader
            // and baked the HLSL into the assembly, so there is no Roslyn work to do.
            // If the source file changed after the build (an editor hot reload), the
            // descriptor is stale and the runtime translator takes over below.
            if (PreferGeneratedDescriptors &&
                DivisionShaderDescriptors.TryGet(shaderType, out DivisionShaderDescriptor? generated) &&
                !IsGeneratedShaderStale(shaderType, generated))
            {
                LogMessage(ShaderLogLevel.Info,
                    $"Shader '{shaderType.Name}': used generated descriptor ({generated.HlslCode.Length:N0} HLSL chars)");

                return HLSLTranslationResult.Success(
                    generated.HlslCode,
                    [.. generated.KernelNames],
                    generated.ShaderTypeName,
                    usedGeneratedDescriptor: true);
            }

            return TranslateShaderFromSource(shaderType);
        }

        /// <summary>
        /// Translates a shader from its C# source with Roslyn. This is the fallback for
        /// shaders without a build-time descriptor: hot reload, shaders generated at
        /// runtime, or <see cref="PreferGeneratedDescriptors"/> set to <c>false</c>.
        /// </summary>
        /// <remarks>
        /// Kept out of line so the generated-descriptor fast path does not pay to JIT
        /// the whole Roslyn pipeline on first use.
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private HLSLTranslationResult TranslateShaderFromSource(Type shaderType)
        {
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

                // One syntax tree per file and one compilation per project/assembly
                // are cached and shared across every TranslateShader call, so a
                // process that compiles many shaders pays the parse and metadata
                // reference cost once instead of once per shader.
                ProjectSourceSet sources = ProjectSourceSet.ForSourceFile(sourcePath);
                SyntaxTree syntaxTree = sources.GetTree(sourcePath);
                CSharpCompilation compilation = sources.GetCompilation(shaderType.Assembly, UseSharedCompilation);
                SemanticModel semanticModel = sources.GetSemanticModel(compilation, syntaxTree);
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
        /// Builds (and caches) the Roslyn reference set for an assembly that
        /// contains shaders.
        /// </summary>
        /// <remarks>
        /// A shader's helper classes and element structs can only be referenced by
        /// the shader's own assembly, so the assembly's reference closure is a
        /// superset of the field-type closure the old per-shader walk produced - and
        /// it is computed once per assembly instead of once per shader. Metadata
        /// references are cached by path as well; Roslyn re-reads a PE image for
        /// every <c>MetadataReference.CreateFromFile</c> call.
        /// </remarks>
        private static List<MetadataReference> BuildMetadataReferences(Assembly shaderAssembly, bool useCache = true)
        {
            if (!useCache)
                return CreateAssemblyReferences(shaderAssembly, useCache: false);

            string key = shaderAssembly.Location;
            if (string.IsNullOrEmpty(key))
                return CreateAssemblyReferences(shaderAssembly, useCache: false);

            return AssemblyReferences.GetOrAdd(key, _ => CreateAssemblyReferences(shaderAssembly));
        }

        private static List<MetadataReference> CreateAssemblyReferences(Assembly shaderAssembly, bool useCache = true)
        {
            // Framework facades first: DivisionMath's public surface mentions
            // System.Runtime types (math.dot returns System.Single), and Roslyn cannot
            // bind those members without the facade reference, which silently turns
            // math.* calls into overload-resolution failures.
            var references = new List<MetadataReference>(FrameworkReferences.Value);
            var referencePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddAssembly(typeof(object).Assembly);
            AddAssembly(typeof(float3).Assembly);
            AddAssembly(typeof(ShaderAttribute).Assembly);
            AddAssembly(shaderAssembly);

            // Element structs may live in an assembly the shader assembly references;
            // resolving the closure keeps them available to the semantic model.
            foreach (AssemblyName name in shaderAssembly.GetReferencedAssemblies())
            {
                try
                {
                    AddAssembly(Assembly.Load(name));
                }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    // A missing optional reference is only reported if the shader
                    // actually mentions a type from it.
                }
            }

            return references;

            void AddAssembly(Assembly assembly)
            {
                if (assembly.IsDynamic)
                    return;

                string? location = assembly.Location;
                if (string.IsNullOrEmpty(location) || !File.Exists(location))
                    return;

                if (!referencePaths.Add(location))
                    return;

                references.Add(useCache ? GetMetadataReference(location) : MetadataReference.CreateFromFile(location));
            }
        }

        private static readonly ConcurrentDictionary<string, List<MetadataReference>> AssemblyReferences =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, MetadataReference> ReferenceCache =
            new(StringComparer.OrdinalIgnoreCase);

        private static MetadataReference GetMetadataReference(string path)
            => ReferenceCache.GetOrAdd(path, static p => MetadataReference.CreateFromFile(p));

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

        private sealed record CachedTree(long WriteTimeTicks, SyntaxTree Tree);

        private static readonly ConcurrentDictionary<string, CachedTree> SourceTreeCache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Parses a source file, reusing the tree while the file is unchanged.
        /// </summary>
        /// <remarks>
        /// One entry per path (not one per revision), so a long hot-reload session
        /// cannot leak a tree per save, and the cache can be filled in parallel.
        /// </remarks>
        private static SyntaxTree GetSyntaxTree(string path)
        {
            long ticks = File.GetLastWriteTimeUtc(path).Ticks;

            if (SourceTreeCache.TryGetValue(path, out CachedTree? cached) && cached.WriteTimeTicks == ticks)
                return cached.Tree;

            SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path);
            SourceTreeCache[path] = new CachedTree(ticks, tree);
            return tree;
        }

        /// <summary>
        /// The syntax trees and the Roslyn compilation of one project, shared by
        /// every shader translated from that project.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The object is refreshed when any <c>.cs</c> file under the project root
        /// changes (the stamp is a hash of path + write time), which keeps hot reload
        /// working while making the steady state - a process that compiles many
        /// shaders from unchanged sources - pay for parsing, references and binding
        /// once per project instead of once per shader.
        /// </para>
        /// <para>
        /// Roslyn compilations and semantic models are safe for concurrent reads, so
        /// shaders can be translated in parallel against the same instance.
        /// </para>
        /// </remarks>
        private sealed class ProjectSourceSet
        {
            private readonly string root;

            // Replaced as a whole when sources change, so concurrent translations keep
            // a consistent snapshot instead of observing a half-cleared dictionary.
            private volatile Dictionary<string, SyntaxTree> treesByPath = new(StringComparer.OrdinalIgnoreCase);
            private readonly ConcurrentDictionary<Assembly, CompilationBundle> compilations = new();

            // Serializes the one-time creation of a project compilation; see
            // GetCompilation for why GetOrAdd is not enough.
            private readonly object compilationGate = new();
            private long stamp;

            private ProjectSourceSet(string root) => this.root = root;

            public static ProjectSourceSet ForSourceFile(string sourcePath)
            {
                string? root = FindProjectRoot(sourcePath)
                    ?? throw new InvalidOperationException(
                        $"Could not find a .csproj above '{sourcePath}'. The runtime translator needs " +
                        "the project sources to resolve helper classes; pass an explicit [Shader] path " +
                        "inside the project, or use the build-time source generator.");

                lock (ProjectCacheLock)
                {
                    if (!ProjectCache.TryGetValue(root, out ProjectSourceSet? set))
                    {
                        set = new ProjectSourceSet(root);
                        ProjectCache[root] = set;
                    }

                    set.RefreshIfChanged();
                    return set;
                }
            }

            public SyntaxTree GetTree(string path)
                => treesByPath.TryGetValue(path, out SyntaxTree? tree) ? tree : GetSyntaxTree(path);

            public CSharpCompilation GetCompilation(Assembly shaderAssembly, bool shared = true)
            {
                Dictionary<string, SyntaxTree> trees = treesByPath;

                if (!shared)
                {
                    // Previous behaviour: a fresh compilation (and fresh references)
                    // per shader. Kept as an escape hatch and as the benchmark's
                    // "before" case.
                    return CSharpCompilation.Create($"{shaderAssembly.GetName().Name}_DivisionShaderCompilation_{Guid.NewGuid():N}")
                        .AddSyntaxTrees(trees.Values)
                        .AddReferences(BuildMetadataReferences(shaderAssembly, useCache: false));
                }

                if (compilations.TryGetValue(shaderAssembly, out CompilationBundle? existing))
                    return existing.Compilation;

                // Build the compilation once even when many shaders are translated in
                // parallel on a cold start: ConcurrentDictionary.GetOrAdd may run its
                // factory on several threads at once, and each run creates a full
                // Roslyn compilation (expensive for a large project).
                lock (compilationGate)
                {
                    if (!compilations.TryGetValue(shaderAssembly, out existing))
                    {
                        existing = new CompilationBundle(
                            CSharpCompilation.Create($"{shaderAssembly.GetName().Name}_DivisionShaderCompilation")
                                .AddSyntaxTrees(trees.Values)
                                .AddReferences(BuildMetadataReferences(shaderAssembly)));

                        compilations[shaderAssembly] = existing;
                    }

                    return existing.Compilation;
                }
            }

            public SemanticModel GetSemanticModel(CSharpCompilation compilation, SyntaxTree tree)
            {
                foreach (CompilationBundle bundle in compilations.Values)
                {
                    if (ReferenceEquals(bundle.Compilation, compilation))
                        return bundle.GetModel(tree);
                }

                return compilation.GetSemanticModel(tree);
            }

            private void RefreshIfChanged()
            {
                List<string> files = EnumerateSourceFiles(root).ToList();
                long newStamp = ComputeStamp(files);
                if (newStamp == stamp && treesByPath.Count > 0)
                    return;

                // Build a new snapshot and publish it atomically: a translation
                // running concurrently keeps using the snapshot it started with.
                // Parsing is CPU-bound and each file is independent, so a large
                // project parses across all cores instead of one file at a time.
                var parsed = new ConcurrentDictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
                Parallel.ForEach(files, file => parsed[file] = GetSyntaxTree(file));

                treesByPath = new Dictionary<string, SyntaxTree>(parsed, StringComparer.OrdinalIgnoreCase);

                // A new compilation is required when the file set changed; the
                // per-assembly bundles are rebuilt on the next request.
                compilations.Clear();
                stamp = newStamp;
            }

            private sealed class CompilationBundle
            {
                private readonly ConcurrentDictionary<SyntaxTree, SemanticModel> models = new();

                public CompilationBundle(CSharpCompilation compilation) => Compilation = compilation;

                public CSharpCompilation Compilation { get; }

                public SemanticModel GetModel(SyntaxTree tree)
                    => models.GetOrAdd(tree, static (t, compilation) => compilation.GetSemanticModel(t), Compilation);
            }
        }

        private static readonly object ProjectCacheLock = new();
        private static readonly Dictionary<string, ProjectSourceSet> ProjectCache = new(StringComparer.OrdinalIgnoreCase);

        private static long ComputeStamp(IReadOnlyList<string> files)
        {
            long hash = 17;
            foreach (string file in files)
            {
                hash = unchecked(hash * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(file));
                hash = unchecked(hash * 31 + File.GetLastWriteTimeUtc(file).Ticks);
            }

            return hash;
        }

        /// <summary>
        /// Finds the nearest directory containing a <c>.csproj</c>, walking up from
        /// the shader's source file.
        /// </summary>
        private static string? FindProjectRoot(string sourcePath)
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
            while (directory is not null)
            {
                if (Directory.EnumerateFiles(directory, "*.csproj").Any())
                    return directory;

                directory = Path.GetDirectoryName(directory);
            }

            return null;
        }

        /// <summary>
        /// Enumerates the C# files that make up the shader's project. Helper classes
        /// live next to the shaders, and their bodies can only be translated when
        /// they are part of the same compilation.
        /// </summary>
        private static IEnumerable<string> EnumerateSourceFiles(string root)
        {
            foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
                    file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                    continue;

                yield return file;
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

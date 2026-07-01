using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DnSpy.Analyzer.Core.Models;

namespace DnSpy.Analyzer.Core
{
    /// <summary>
    /// Provides decompilation services.
    /// Uses ICSharpCode.Decompiler when available; falls back gracefully.
    /// </summary>
    public class DecompilationHelper : IDisposable
    {
        private readonly AssemblyAnalyzer _analyzer;
        private bool _disposed;

        // ICSharpCode.Decompiler types - loaded lazily
        private readonly bool _decompilerAvailable;
        private readonly Type? _csharpDecompilerType;
        private readonly Type? _decompilerSettingsType;

        public DecompilationHelper(AssemblyAnalyzer? analyzer = null)
        {
            _analyzer = analyzer ?? new AssemblyAnalyzer();

            // Try to load ICSharpCode.Decompiler
            try
            {
                var asm = System.Reflection.Assembly.Load("ICSharpCode.Decompiler");
                _csharpDecompilerType = asm.GetType("ICSharpCode.Decompiler.CSharp.CSharpDecompiler");
                _decompilerSettingsType = asm.GetType("ICSharpCode.Decompiler.DecompilerSettings");
                _decompilerAvailable = _csharpDecompilerType != null;
            }
            catch
            {
                _decompilerAvailable = false;
            }
        }

        public AnalysisResult<DecompileResult> DecompileMethod(string path, string typeFullName, string methodName)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (!File.Exists(path))
                    return AnalysisResult<DecompileResult>.Fail($"File not found: {path}", sw.ElapsedMilliseconds);
                if (!_decompilerAvailable)
                    return AnalysisResult<DecompileResult>.Fail("ICSharpCode.Decompiler not available", sw.ElapsedMilliseconds);

                // Create CSharpDecompiler instance via reflection
                var settings = Activator.CreateInstance(_decompilerSettingsType!);
                var decompiler = Activator.CreateInstance(_csharpDecompilerType!, new object[] { path, settings });

                // Get type system
                var tsProp = _csharpDecompilerType!.GetProperty("TypeSystem");
                var typeSystem = tsProp!.GetValue(decompiler);
                var mainMod = typeSystem!.GetType().GetProperty("MainModule")!.GetValue(typeSystem);
                var typeDefs = mainMod!.GetType().GetProperty("TypeDefinitions")!.GetValue(mainMod) as System.Collections.IEnumerable;

                // Find the type
                object? typeDef = null;
                foreach (var t in typeDefs!)
                {
                    var fn = t.GetType().GetProperty("FullName")!.GetValue(t) as string;
                    if (fn != null && fn.Equals(typeFullName, StringComparison.OrdinalIgnoreCase))
                    { typeDef = t; break; }
                }
                if (typeDef == null)
                    return AnalysisResult<DecompileResult>.Fail($"Type not found: {typeFullName}", sw.ElapsedMilliseconds);

                // Find the method
                object? methodDef = null;
                var methods = typeDef.GetType().GetProperty("Methods")!.GetValue(typeDef) as System.Collections.IEnumerable;
                foreach (var m in methods!)
                {
                    var mn = m.GetType().GetProperty("Name")!.GetValue(m) as string;
                    if (mn != null && mn.Equals(methodName, StringComparison.OrdinalIgnoreCase))
                    { methodDef = m; break; }
                }
                if (methodDef == null)
                    return AnalysisResult<DecompileResult>.Fail($"Method '{methodName}' not found", sw.ElapsedMilliseconds);

                // Get metadata token and decompile
                var metaToken = methodDef.GetType().GetProperty("MetadataToken")!.GetValue(methodDef);
                var decompileMethod = _csharpDecompilerType.GetMethod("Decompile", new[] { metaToken!.GetType() });
                var syntaxTree = decompileMethod!.Invoke(decompiler, new[] { metaToken });
                var code = syntaxTree!.ToString();

                return AnalysisResult<DecompileResult>.Ok(new DecompileResult
                {
                    CSharpCode = code,
                    MethodName = methodName,
                    TypeName = typeFullName
                }, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                return AnalysisResult<DecompileResult>.Fail($"Decompilation failed: {ex.Message}", sw.ElapsedMilliseconds);
            }
        }

        public AnalysisResult<DecompileResult> DecompileType(string path, string typeFullName)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (!File.Exists(path))
                    return AnalysisResult<DecompileResult>.Fail($"File not found: {path}", sw.ElapsedMilliseconds);
                if (!_decompilerAvailable)
                    return AnalysisResult<DecompileResult>.Fail("ICSharpCode.Decompiler not available", sw.ElapsedMilliseconds);

                // Use DecompileTypeAsString if available, else fallback
                var decompileTypeAsString = _csharpDecompilerType!.GetMethod("DecompileTypeAsString");
                if (decompileTypeAsString != null)
                {
                    var settings = Activator.CreateInstance(_decompilerSettingsType!);
                    var decompiler = Activator.CreateInstance(_csharpDecompilerType!, new object[] { path, settings });

                    // Create FullTypeName
                    var fullTypeNameType = _csharpDecompilerType.Assembly.GetType("ICSharpCode.Decompiler.FullTypeName");
                    var ftn = Activator.CreateInstance(fullTypeNameType!, new object[] { typeFullName });
                    var code = (string)decompileTypeAsString.Invoke(decompiler, new[] { ftn });
                    return AnalysisResult<DecompileResult>.Ok(new DecompileResult { CSharpCode = code, TypeName = typeFullName }, sw.ElapsedMilliseconds);
                }

                return AnalysisResult<DecompileResult>.Fail("DecompileTypeAsString not available", sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                return AnalysisResult<DecompileResult>.Fail($"Type decompilation failed: {ex.Message}", sw.ElapsedMilliseconds);
            }
        }

        public AnalysisResult<string> GetILText(string path, string typeFullName, string methodName)
        {
            var sw = Stopwatch.StartNew();
            return AnalysisResult<string>.Fail("IL text requires dnlib which is not included", sw.ElapsedMilliseconds);
        }

        public void Dispose()
        {
            if (!_disposed) _disposed = true;
        }
    }
}

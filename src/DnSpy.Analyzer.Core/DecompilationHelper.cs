using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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

                // Find the method handle using System.Reflection.Metadata
                EntityHandle? methodHandle = null;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (var peReader = new PEReader(fs, PEStreamOptions.PrefetchEntireImage))
                {
                    if (!peReader.HasMetadata)
                        return AnalysisResult<DecompileResult>.Fail("Not a .NET assembly", sw.ElapsedMilliseconds);

                    var md = peReader.GetMetadataReader();

                    // Find the type
                    TypeDefinitionHandle? typeHandle = null;
                    foreach (var tdh in md.TypeDefinitions)
                    {
                        var td = md.GetTypeDefinition(tdh);
                        var ns = md.GetString(td.Namespace);
                        var name = md.GetString(td.Name);
                        var fn = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
                        if (fn.Equals(typeFullName, StringComparison.OrdinalIgnoreCase))
                        { typeHandle = tdh; break; }
                    }

                    if (typeHandle == null)
                        return AnalysisResult<DecompileResult>.Fail($"Type not found: {typeFullName}", sw.ElapsedMilliseconds);

                    // Find the method within the type
                    var typeDef = md.GetTypeDefinition(typeHandle.Value);
                    foreach (var mh in typeDef.GetMethods())
                    {
                        var m = md.GetMethodDefinition(mh);
                        var mn = md.GetString(m.Name);
                        if (mn.Equals(methodName, StringComparison.OrdinalIgnoreCase))
                        { methodHandle = mh; break; }
                    }
                }

                if (methodHandle == null)
                    return AnalysisResult<DecompileResult>.Fail($"Method '{methodName}' not found in {typeFullName}", sw.ElapsedMilliseconds);

                // Create CSharpDecompiler and decompile the method directly
                var settings = Activator.CreateInstance(_decompilerSettingsType!);
                var decompiler = Activator.CreateInstance(_csharpDecompilerType!, new object[] { path, settings });

                // Call Decompile(EntityHandle[]) — takes an array of handles
                var decompileMethod = _csharpDecompilerType!.GetMethod("Decompile", new[] { typeof(EntityHandle[]) });
                if (decompileMethod == null)
                    return AnalysisResult<DecompileResult>.Fail("Decompile(EntityHandle) method not found", sw.ElapsedMilliseconds);

                var syntaxTree = decompileMethod.Invoke(decompiler, new object[] { new EntityHandle[] { methodHandle!.Value } });
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
                    var fullTypeNameType = _csharpDecompilerType.Assembly.GetType("ICSharpCode.Decompiler.TypeSystem.FullTypeName");
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

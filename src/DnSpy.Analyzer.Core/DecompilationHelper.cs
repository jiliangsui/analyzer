using System;
using System.Diagnostics;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DnSpy.Analyzer.Core.Models;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;

namespace DnSpy.Analyzer.Core
{
    /// <summary>
    /// Provides decompilation services using the ICSharpCode.Decompiler (ILSpy) engine.
    /// </summary>
    public class DecompilationHelper : IDisposable
    {
        private bool _disposed;

        public AnalysisResult<DecompileResult> DecompileMethod(string path, string typeFullName, string methodName)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (!File.Exists(path))
                    return AnalysisResult<DecompileResult>.Fail($"File not found: {path}", sw.ElapsedMilliseconds);

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                using var peReader = new PEReader(fs, PEStreamOptions.PrefetchEntireImage);
                if (!peReader.HasMetadata)
                    return AnalysisResult<DecompileResult>.Fail("Not a .NET assembly", sw.ElapsedMilliseconds);

                var md = peReader.GetMetadataReader();
                var typeHandle = AssemblyAnalyzer.FindTypeHandle(md, typeFullName);
                if (typeHandle.IsNil)
                    return AnalysisResult<DecompileResult>.Fail($"Type not found: {typeFullName}", sw.ElapsedMilliseconds);

                var methodHandle = AssemblyAnalyzer.FindMethodHandle(md, typeHandle, methodName);
                if (methodHandle.IsNil)
                    return AnalysisResult<DecompileResult>.Fail($"Method '{methodName}' not found in {typeFullName}", sw.ElapsedMilliseconds);

                var decompiler = new CSharpDecompiler(path, new DecompilerSettings());
                var code = decompiler.Decompile(new[] { (EntityHandle)methodHandle }).ToString();

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

                var decompiler = new CSharpDecompiler(path, new DecompilerSettings());
                var code = decompiler.DecompileTypeAsString(new FullTypeName(typeFullName));

                return AnalysisResult<DecompileResult>.Ok(new DecompileResult
                {
                    CSharpCode = code,
                    TypeName = typeFullName
                }, sw.ElapsedMilliseconds);
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
            GC.SuppressFinalize(this);
        }
    }
}

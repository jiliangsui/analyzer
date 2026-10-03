using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DnSpy.Analyzer.Core.Models;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace DnSpy.Analyzer.Core
{
    /// <summary>
    /// Provides decompilation services using the ICSharpCode.Decompiler (ILSpy) engine.
    ///
    /// Reference resolution: by default the decompiler only probes the target assembly's own
    /// directory. Unity / older .NET Framework assemblies frequently live in a separate folder
    /// (e.g. a shared "Managed" folder next to the game binary), so callers can pass extra
    /// probe directories via <c>referencePaths</c>.
    /// </summary>
    public class DecompilationHelper : IDisposable
    {
        private bool _disposed;

        /// <summary>
        /// Extra directories probed for referenced assemblies, in addition to the target
        /// assembly's directory and the runtime's framework directory.
        /// </summary>
        public IList<string> ReferencePaths { get; } = new List<string>();

        public DecompilationHelper() { }

        public DecompilationHelper(IEnumerable<string>? referencePaths)
        {
            if (referencePaths != null)
            {
                foreach (var p in referencePaths)
                    AddReferencePath(p);
            }
        }

        /// <summary>
        /// Registers a probe directory (or a single assembly file) for dependency resolution.
        /// Silently ignores entries that do not exist so a mixed list of paths stays usable.
        /// </summary>
        public void AddReferencePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            string full;
            try { full = Path.GetFullPath(path); }
            catch { return; }

            // Accept both directories and individual assembly files.
            if (File.Exists(full))
            {
                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir) && !ReferencePaths.Contains(dir))
                    ReferencePaths.Add(dir);
            }
            else if (Directory.Exists(full))
            {
                if (!ReferencePaths.Contains(full))
                    ReferencePaths.Add(full);
            }
        }

        /// <summary>
        /// Builds a decompiler for <paramref name="path"/> wired to a reference resolver that
        /// probes: the target directory, every registered reference path, then the runtime
        /// framework directory (handles mscorlib / System.* on modern .NET).
        /// </summary>
        private CSharpDecompiler CreateDecompiler(string path)
        {
            var settings = new DecompilerSettings();

            // The resolver returns null for unresolvable references; this setting keeps
            // ILSpy from aborting the whole decompilation because of them — the affected
            // members just render with unresolved identifiers instead.
            settings.ThrowOnAssemblyResolveErrors = false;

            var resolver = BuildResolver(path);
            return new CSharpDecompiler(path, resolver, settings);
        }

        private UniversalAssemblyResolver BuildResolver(string path)
        {
            // PEStreamOptions default is fine here; the resolver reads headers lazily.
            var resolver = new UniversalAssemblyResolver(path, throwOnError: false, targetFramework: null);

            var probeDirs = new List<string>();

            var targetDir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(targetDir)) probeDirs.Add(targetDir);
            probeDirs.AddRange(ReferencePaths);

            // Framework directory of the running runtime — supplies mscorlib/System.Private.CoreLib
            // when decompiling old .NET Framework assemblies (e.g. Unity 5.x Mono output).
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
            if (!string.IsNullOrEmpty(runtimeDir)) probeDirs.Add(runtimeDir);

            foreach (var dir in probeDirs.Distinct())
            {
                if (Directory.Exists(dir))
                    resolver.AddSearchDirectory(dir);
            }

            return resolver;
        }

        /// <summary>
        /// Reports which referenced assemblies could not be resolved for the given target.
        /// Useful for diagnosing the "Failed to resolve assembly: UnityEngine" family of errors
        /// before attempting a decompile.
        /// </summary>
        public AnalysisResult<ReferenceReport> CheckReferences(string path)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (!File.Exists(path))
                    return AnalysisResult<ReferenceReport>.Fail($"File not found: {path}", sw.ElapsedMilliseconds);

                var report = new ReferenceReport { AssemblyPath = path };

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                using var peReader = new PEReader(fs, PEStreamOptions.PrefetchEntireImage);
                if (!peReader.HasMetadata)
                    return AnalysisResult<ReferenceReport>.Fail("Not a .NET assembly", sw.ElapsedMilliseconds);

                var resolver = BuildResolver(path);

                var resolverDirs = new List<string>();
                var targetDir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(targetDir)) resolverDirs.Add(targetDir);
                resolverDirs.AddRange(ReferencePaths);
                report.ProbedDirectories = resolverDirs.Distinct().ToList();

                using var module = new PEFile(path);
                var md = peReader.GetMetadataReader();

                foreach (var aRefHandle in md.AssemblyReferences)
                {
                    var aRef = md.GetAssemblyReference(aRefHandle);
                    var name = md.GetString(aRef.Name);

                    string? resolved = null;
                    try
                    {
                        // ILSpy exposes a single-arg Parse on AssemblyNameReference; build a
                        // fully-qualified name so the resolver matches the right version.
                        var display = $"{name}, Version={aRef.Version}, Culture=neutral, PublicKeyToken=null";
                        var reference = AssemblyNameReference.Parse(display);
                        var candidate = resolver.Resolve(reference);
                        resolved = candidate?.FileName;
                    }
                    catch
                    {
                        // fall through — recorded as unresolved below
                    }

                    report.References.Add(new ReferenceStatus
                    {
                        Name = name,
                        Version = aRef.Version.ToString(),
                        Resolved = resolved != null,
                        ResolvedPath = resolved
                    });
                }

                report.UnresolvedCount = report.References.Count(r => !r.Resolved);
                return AnalysisResult<ReferenceReport>.Ok(report, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                return AnalysisResult<ReferenceReport>.Fail($"Reference check failed: {ex.Message}", sw.ElapsedMilliseconds);
            }
        }

        public AnalysisResult<DecompileResult> DecompileMethod(string path, string typeFullName, string methodName, string signatureFilter = null)
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

                var candidates = AssemblyAnalyzer.FindMethodHandles(md, typeHandle, methodName)
                    .Select(h => (Handle: h, Detail: AssemblyAnalyzer.BuildMethodDetail(md, md.GetMethodDefinition(h))))
                    .ToList();
                if (candidates.Count == 0)
                    return AnalysisResult<DecompileResult>.Fail($"Method '{methodName}' not found in {typeFullName}", sw.ElapsedMilliseconds);

                List<(MethodDefinitionHandle Handle, MethodDetail Detail)> selected;
                if (string.IsNullOrEmpty(signatureFilter))
                {
                    selected = candidates;
                }
                else
                {
                    selected = candidates
                        .Where(x => x.Detail.FullSignature.IndexOf(signatureFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList();
                    if (selected.Count == 0)
                        return AnalysisResult<DecompileResult>.Fail(
                            $"No overload of '{methodName}' matches signature filter '{signatureFilter}'. " +
                            $"Candidates: {RenderCandidates(candidates)}", sw.ElapsedMilliseconds);
                }

                if (selected.Count > 1)
                    return AnalysisResult<DecompileResult>.Fail(
                        $"'{methodName}' has {selected.Count} overloads in {typeFullName}; " +
                        $"disambiguate with --signature. Candidates: {RenderCandidates(selected)}", sw.ElapsedMilliseconds);

                var decompiler = CreateDecompiler(path);
                var code = decompiler.Decompile(new[] { (EntityHandle)selected[0].Handle }).ToString();

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

                // Resolve the type first so dotted nested names ("Ns.Outer.Inner") work,
                // then build the ILSpy FullTypeName ("Ns.Outer/Inner") from the definition.
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                using var peReader = new PEReader(fs, PEStreamOptions.PrefetchEntireImage);
                if (!peReader.HasMetadata)
                    return AnalysisResult<DecompileResult>.Fail("Not a .NET assembly", sw.ElapsedMilliseconds);

                var md = peReader.GetMetadataReader();
                var typeHandle = AssemblyAnalyzer.FindTypeHandle(md, typeFullName);
                if (typeHandle.IsNil)
                    return AnalysisResult<DecompileResult>.Fail($"Type not found: {typeFullName}", sw.ElapsedMilliseconds);

                var decompiler = CreateDecompiler(path);
                var ilSpyName = AssemblyAnalyzer.GetILSpyFullTypeName(md, md.GetTypeDefinition(typeHandle));
                var code = decompiler.DecompileTypeAsString(new FullTypeName(ilSpyName));

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

        private static string RenderCandidates(List<(MethodDefinitionHandle Handle, MethodDetail Detail)> candidates) =>
            string.Join(" | ", candidates.Select(x => x.Detail.FullSignature));

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

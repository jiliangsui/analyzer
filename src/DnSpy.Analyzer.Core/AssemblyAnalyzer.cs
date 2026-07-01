using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DnSpy.Analyzer.Core.Models;

namespace DnSpy.Analyzer.Core
{
    public class AssemblyAnalyzer
    {
        public AnalysisResult<FolderScanResult> ScanFolder(string path, bool recursive = true)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (!Directory.Exists(path))
                    return AnalysisResult<FolderScanResult>.Fail($"Directory not found: {path}", sw.ElapsedMilliseconds);

                var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                var files = Directory.EnumerateFiles(path, "*.*", searchOption)
                    .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var result = new FolderScanResult { FolderPath = path };

                foreach (var file in files)
                {
                    try
                    {
                        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read);
                        if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z')
                        { result.SkippedFiles.Add(file); continue; }
                        fs.Position = 0;

                        using var peReader = new PEReader(fs, PEStreamOptions.PrefetchEntireImage);
                        if (!peReader.HasMetadata)
                        { result.SkippedFiles.Add(file); continue; }

                        var mdReader = peReader.GetMetadataReader();
                        var summary = new AssemblySummary
                        {
                            FilePath = file,
                            Name = Path.GetFileNameWithoutExtension(file),
                            IsManaged = true,
                            IsDotNet = true,
                            FileSize = new FileInfo(file).Length,
                            Architecture = peReader.PEHeaders.PEHeader?.Magic == PEMagic.PE32Plus ? "x64" : "x86",
                        };

                        if (!mdReader.IsAssembly)
                        {
                            // module without assembly manifest (e.g. netmodule)
                            result.Assemblies.Add(summary);
                            continue;
                        }

                        var asmDef = mdReader.GetAssemblyDefinition();
                        summary.Name = mdReader.GetString(asmDef.Name);
                        summary.Version = asmDef.Version.ToString();
                        var culture = mdReader.GetString(asmDef.Culture);
                        summary.Culture = string.IsNullOrEmpty(culture) ? null : culture;
                        var pkt = asmDef.PublicKey;
                        if (!pkt.IsNil)
                        {
                            var bytes = mdReader.GetBlobBytes(pkt);
                            summary.PublicKeyToken = bytes != null ? BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant() : null;
                        }

                        result.Assemblies.Add(summary);
                    }
                    catch
                    {
                        result.SkippedFiles.Add(file);
                    }
                }

                return AnalysisResult<FolderScanResult>.Ok(result, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                return AnalysisResult<FolderScanResult>.Fail($"Scan failed: {ex.Message}", sw.ElapsedMilliseconds);
            }
        }

        private static MetadataReader GetReader(string path)
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            var peReader = new PEReader(fs, PEStreamOptions.PrefetchEntireImage | PEStreamOptions.LeaveOpen);
            return peReader.GetMetadataReader();
        }

        public AnalysisResult<AssemblyDetail> AnalyzeAssembly(string path)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (!File.Exists(path))
                    return AnalysisResult<AssemblyDetail>.Fail($"File not found: {path}", sw.ElapsedMilliseconds);

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                using var peReader = new PEReader(fs, PEStreamOptions.PrefetchEntireImage);
                if (!peReader.HasMetadata)
                    return AnalysisResult<AssemblyDetail>.Fail("Not a .NET assembly", sw.ElapsedMilliseconds);

                var md = peReader.GetMetadataReader();
                var detail = new AssemblyDetail
                {
                    Assembly = new AssemblySummary
                    {
                        FilePath = path,
                        Name = Path.GetFileNameWithoutExtension(path),
                        IsManaged = true,
                        IsDotNet = true,
                        FileSize = new FileInfo(path).Length,
                    },
                    Dependencies = new List<AssemblyDependency>()
                };

                if (md.IsAssembly)
                {
                    var asmDef = md.GetAssemblyDefinition();
                    detail.Assembly.Name = md.GetString(asmDef.Name);
                    detail.Assembly.Version = asmDef.Version.ToString();
                    var culture = md.GetString(asmDef.Culture);
                    detail.Assembly.Culture = string.IsNullOrEmpty(culture) ? null : culture;
                }

                // Assembly references
                foreach (var aRefHandle in md.AssemblyReferences)
                {
                    var aRef = md.GetAssemblyReference(aRefHandle);
                    detail.Dependencies.Add(new AssemblyDependency
                    {
                        Name = md.GetString(aRef.Name),
                        Version = aRef.Version.ToString()
                    });
                }

                // Namespace groups
                var nsGroups = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                int typeCount = 0;

                foreach (var tdh in md.TypeDefinitions)
                {
                    var td = md.GetTypeDefinition(tdh);
                    if ((td.Attributes & TypeAttributes.NestedFamANDAssem) != 0) continue; // skip nested
                    if (td.Name.IsNil) continue;

                    var ns = md.GetString(td.Namespace);
                    if (string.IsNullOrEmpty(ns)) ns = "(global)";
                    nsGroups.TryGetValue(ns, out int count);
                    nsGroups[ns] = count + 1;
                    typeCount++;
                }

                detail.Namespaces = nsGroups
                    .Select(kv => new NamespaceGroup { Namespace = kv.Key, TypeCount = kv.Value })
                    .OrderBy(n => n.Namespace).ToList();
                detail.TotalTypes = typeCount;

                return AnalysisResult<AssemblyDetail>.Ok(detail, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                return AnalysisResult<AssemblyDetail>.Fail($"Analysis failed: {ex.Message}", sw.ElapsedMilliseconds);
            }
        }

        public AnalysisResult<List<TypeBrief>> ListTypes(string path, string? namespaceFilter = null, int offset = 0, int limit = 200)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (!File.Exists(path))
                    return AnalysisResult<List<TypeBrief>>.Fail($"File not found: {path}", sw.ElapsedMilliseconds);

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                using var peReader = new PEReader(fs, PEStreamOptions.PrefetchEntireImage);
                if (!peReader.HasMetadata)
                    return AnalysisResult<List<TypeBrief>>.Fail("Not a .NET assembly", sw.ElapsedMilliseconds);

                var md = peReader.GetMetadataReader();
                var types = new List<TypeBrief>();

                foreach (var tdh in md.TypeDefinitions)
                {
                    var td = md.GetTypeDefinition(tdh);
                    var ns = md.GetString(td.Namespace);

                    if (!string.IsNullOrEmpty(namespaceFilter) &&
                        !ns.StartsWith(namespaceFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Skip nested types for top-level listing
                    bool isNested = (td.Attributes & TypeAttributes.NestedFamANDAssem) != 0;

                    types.Add(new TypeBrief
                    {
                        FullName = (string.IsNullOrEmpty(ns) ? "" : ns + ".") + md.GetString(td.Name),
                        Name = md.GetString(td.Name),
                        Namespace = ns,
                        Kind = GetTypeKind(td),
                        AccessLevel = GetAccessLevel(td),
                        MethodCount = td.GetMethods().Count,
                        FieldCount = td.GetFields().Count,
                        PropertyCount = td.GetProperties().Count,
                        EventCount = td.GetEvents().Count,
                        IsNested = isNested,
                    });
                }

                var paged = types.OrderBy(t => t.FullName).Skip(offset).Take(limit).ToList();
                return AnalysisResult<List<TypeBrief>>.Ok(paged, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                return AnalysisResult<List<TypeBrief>>.Fail($"List types failed: {ex.Message}", sw.ElapsedMilliseconds);
            }
        }

        public AnalysisResult<TypeDetail> GetTypeDetail(string path, string typeFullName)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (!File.Exists(path))
                    return AnalysisResult<TypeDetail>.Fail($"File not found: {path}", sw.ElapsedMilliseconds);

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                using var peReader = new PEReader(fs, PEStreamOptions.PrefetchEntireImage);
                if (!peReader.HasMetadata)
                    return AnalysisResult<TypeDetail>.Fail("Not a .NET assembly", sw.ElapsedMilliseconds);

                var md = peReader.GetMetadataReader();
                var (typeHandle, typeDef) = FindType(md, typeFullName);
                if (typeHandle.IsNil)
                    return AnalysisResult<TypeDetail>.Fail($"Type not found: {typeFullName}", sw.ElapsedMilliseconds);

                var ns = md.GetString(typeDef.Namespace);
                var name = md.GetString(typeDef.Name);
                var detail = new TypeDetail
                {
                    FullName = (string.IsNullOrEmpty(ns) ? "" : ns + ".") + name,
                    Kind = GetTypeKind(typeDef),
                    AccessLevel = GetAccessLevel(typeDef),
                    IsSealed = (typeDef.Attributes & TypeAttributes.Sealed) != 0,
                    IsAbstract = (typeDef.Attributes & TypeAttributes.Abstract) != 0,
                    IsStatic = (typeDef.Attributes & (TypeAttributes.Abstract | TypeAttributes.Sealed)) == (TypeAttributes.Abstract | TypeAttributes.Sealed),
                };

                // Base type
                if (!typeDef.BaseType.IsNil)
                {
                    var bt = md.GetTypeDefinition((TypeDefinitionHandle)typeDef.BaseType);
                    // For base types that are references, we try to get a reasonable name
                    try
                    {
                        var btHandle = (TypeDefinitionHandle)typeDef.BaseType;
                        var btDef = md.GetTypeDefinition(btHandle);
                        detail.BaseType = md.GetString(btDef.Namespace) + "." + md.GetString(btDef.Name);
                    }
                    catch
                    {
                        detail.BaseType = "[base type]";
                    }
                }

                // Methods
                foreach (var mh in typeDef.GetMethods())
                {
                    var m = md.GetMethodDefinition(mh);
                    var mName = md.GetString(m.Name);
                    detail.Methods.Add(new MethodDetail
                    {
                        Name = mName,
                        FullSignature = mName,
                        ReturnType = m.DecodeSignature(new DisassemblingSignatureTypeProvider(), default).ReturnType,
                        AccessLevel = GetMethodAccess(m),
                        IsStatic = (m.Attributes & MethodAttributes.Static) != 0,
                        IsVirtual = (m.Attributes & MethodAttributes.Virtual) != 0,
                        IsAbstract = (m.Attributes & MethodAttributes.Abstract) != 0,
                        IsSealed = (m.Attributes & MethodAttributes.Final) != 0,
                        IsConstructor = mName == ".ctor" || mName == ".cctor",
                        IsGetter = mName.StartsWith("get_"),
                        IsSetter = mName.StartsWith("set_"),
                    });
                }

                // Fields
                foreach (var fh in typeDef.GetFields())
                {
                    var f = md.GetFieldDefinition(fh);
                    detail.Fields.Add(new MemberBrief
                    {
                        Name = md.GetString(f.Name),
                        Kind = "Field",
                        AccessLevel = GetFieldAccess(f),
                    });
                }

                // Properties
                foreach (var ph in typeDef.GetProperties())
                {
                    var p = md.GetPropertyDefinition(ph);
                    detail.Properties.Add(new MemberBrief
                    {
                        Name = md.GetString(p.Name),
                        Kind = "Property",
                    });
                }

                // Events
                foreach (var eh in typeDef.GetEvents())
                {
                    var e = md.GetEventDefinition(eh);
                    detail.Events.Add(new MemberBrief
                    {
                        Name = md.GetString(e.Name),
                        Kind = "Event",
                    });
                }

                return AnalysisResult<TypeDetail>.Ok(detail, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                return AnalysisResult<TypeDetail>.Fail($"Get type detail failed: {ex.Message}", sw.ElapsedMilliseconds);
            }
        }

        public AnalysisResult<List<MethodDetail>> ListMethods(string path, string typeFullName)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (!File.Exists(path))
                    return AnalysisResult<List<MethodDetail>>.Fail($"File not found: {path}", sw.ElapsedMilliseconds);

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                using var peReader = new PEReader(fs, PEStreamOptions.PrefetchEntireImage);
                if (!peReader.HasMetadata)
                    return AnalysisResult<List<MethodDetail>>.Fail("Not a .NET assembly", sw.ElapsedMilliseconds);

                var md = peReader.GetMetadataReader();
                var (th, td) = FindType(md, typeFullName);
                if (th.IsNil)
                    return AnalysisResult<List<MethodDetail>>.Fail($"Type not found: {typeFullName}", sw.ElapsedMilliseconds);

                var methods = new List<MethodDetail>();
                foreach (var mh in td.GetMethods())
                {
                    var m = md.GetMethodDefinition(mh);
                    var mName = md.GetString(m.Name);
                    var sig = m.DecodeSignature(
                        new DisassemblingSignatureTypeProvider(), default);
                    methods.Add(new MethodDetail
                    {
                        Name = mName,
                        FullSignature = mName,
                        ReturnType = sig.ReturnType,
                        AccessLevel = GetMethodAccess(m),
                        IsStatic = (m.Attributes & MethodAttributes.Static) != 0,
                        IsVirtual = (m.Attributes & MethodAttributes.Virtual) != 0,
                        IsAbstract = (m.Attributes & MethodAttributes.Abstract) != 0,
                        IsOverride = (m.Attributes & MethodAttributes.Virtual) != 0 &&
                                     (m.Attributes & MethodAttributes.NewSlot) == 0,
                        IsConstructor = mName == ".ctor" || mName == ".cctor",
                        IsGetter = mName.StartsWith("get_"),
                        IsSetter = mName.StartsWith("set_"),
                    });
                }

                return AnalysisResult<List<MethodDetail>>.Ok(methods, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                return AnalysisResult<List<MethodDetail>>.Fail($"List methods failed: {ex.Message}", sw.ElapsedMilliseconds);
            }
        }

        private static (TypeDefinitionHandle, TypeDefinition) FindType(MetadataReader md, string fullName)
        {
            foreach (var tdh in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(tdh);
                var ns = md.GetString(td.Namespace);
                var name = md.GetString(td.Name);
                var fn = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
                if (fn.Equals(fullName, StringComparison.OrdinalIgnoreCase))
                    return (tdh, td);
            }
            return (default, default);
        }

        #region Helpers

        private static string GetTypeKind(TypeDefinition td)
        {
            if ((td.Attributes & TypeAttributes.Interface) != 0) return "Interface";
            if ((td.Attributes & TypeAttributes.Class) != 0)
            {
                // Check if it's a value type (struct)
                // For simplicity, check base type
                return "Class"; // simplified - SRM doesn't easily differentiate struct/enum
            }
            return "Class";
        }

        private static string GetAccessLevel(TypeDefinition td)
        {
            var attrs = td.Attributes & TypeAttributes.VisibilityMask;
            return attrs switch
            {
                TypeAttributes.Public => "public",
                TypeAttributes.NestedPublic => "public",
                TypeAttributes.NestedFamily => "protected",
                TypeAttributes.NestedPrivate => "private",
                TypeAttributes.NestedAssembly => "internal",
                _ => "internal"
            };
        }

        private static string GetMethodAccess(MethodDefinition m)
        {
            return (m.Attributes & MethodAttributes.MemberAccessMask) switch
            {
                MethodAttributes.Public => "public",
                MethodAttributes.Family => "protected",
                MethodAttributes.Private => "private",
                MethodAttributes.Assembly => "internal",
                MethodAttributes.FamORAssem => "protected internal",
                _ => ""
            };
        }

        private static string GetFieldAccess(FieldDefinition f)
        {
            return (f.Attributes & FieldAttributes.FieldAccessMask) switch
            {
                FieldAttributes.Public => "public",
                FieldAttributes.Family => "protected",
                FieldAttributes.Private => "private",
                FieldAttributes.Assembly => "internal",
                _ => ""
            };
        }

        private static string GetTypeNameFromSignature(MetadataReader md, string sig)
        {
            return sig;
        }

        class DisassemblingSignatureTypeProvider : ISignatureTypeProvider<string, object?>
        {
            public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
            public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            {
                var td = reader.GetTypeDefinition(handle);
                return reader.GetString(td.Namespace) + "." + reader.GetString(td.Name);
            }
            public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            {
                var tr = reader.GetTypeReference(handle);
                return reader.GetString(tr.Namespace) + "." + reader.GetString(tr.Name);
            }
            public string GetSZArrayType(string elementType) => elementType + "[]";
            public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";
            public string GetByReferenceType(string elementType) => "ref " + elementType;
            public string GetPointerType(string elementType) => elementType + "*";
            public string GetPinnedType(string elementType) => elementType;
            public string GetGenericInstantiation(string genericType, System.Collections.Immutable.ImmutableArray<string> typeArguments)
                => genericType + "<" + string.Join(", ", typeArguments) + ">";
            public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
            public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
            public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
            public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
            public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            {
                var ts = reader.GetTypeSpecification(handle);
                return ts.DecodeSignature(this, genericContext);
            }
        }

        #endregion
    }
}

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
                            RuntimeVersion = GetRuntimeVersion(peReader),
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
                        Architecture = peReader.PEHeaders.PEHeader?.Magic == PEMagic.PE32Plus ? "x64" : "x86",
                        RuntimeVersion = GetRuntimeVersion(peReader),
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
                    if (IsNested(td)) continue; // skip nested
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
                    var dottedName = GetDottedFullTypeName(md, td);

                    // Match on the full dotted name so nested types ("Ns.Outer.Nested")
                    // survive a namespace filter even though their own Namespace is empty.
                    if (!string.IsNullOrEmpty(namespaceFilter) &&
                        !dottedName.StartsWith(namespaceFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    bool isNested = IsNested(td);

                    var brief = new TypeBrief
                    {
                        FullName = dottedName,
                        Name = md.GetString(td.Name),
                        Namespace = ns,
                        Kind = GetTypeKind(md, td),
                        AccessLevel = GetAccessLevel(td),
                        MethodCount = td.GetMethods().Count,
                        FieldCount = td.GetFields().Count,
                        PropertyCount = td.GetProperties().Count,
                        EventCount = td.GetEvents().Count,
                        IsNested = isNested,
                    };
                    if (isNested)
                    {
                        var declaringHandle = td.GetDeclaringType();
                        if (!declaringHandle.IsNil)
                            brief.DeclaringType = GetDottedFullTypeName(md, md.GetTypeDefinition(declaringHandle));
                    }
                    types.Add(brief);
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
                    FullName = GetDottedFullTypeName(md, typeDef),
                    Kind = GetTypeKind(md, typeDef),
                    AccessLevel = GetAccessLevel(typeDef),
                    IsSealed = (typeDef.Attributes & TypeAttributes.Sealed) != 0,
                    IsAbstract = (typeDef.Attributes & TypeAttributes.Abstract) != 0,
                    IsStatic = (typeDef.Attributes & (TypeAttributes.Abstract | TypeAttributes.Sealed)) == (TypeAttributes.Abstract | TypeAttributes.Sealed),
                };

                foreach (var gph in typeDef.GetGenericParameters())
                    detail.GenericParameters.Add(md.GetString(md.GetGenericParameter(gph).Name));

                foreach (var cah in typeDef.GetCustomAttributes())
                {
                    var attrName = GetCustomAttributeName(md, cah);
                    if (attrName != null) detail.Attributes.Add(attrName);
                }

                // Base type (may be a definition, reference, or generic specification)
                if (!typeDef.BaseType.IsNil)
                {
                    try
                    {
                        detail.BaseType = GetTypeName(md, typeDef.BaseType);
                    }
                    catch
                    {
                        detail.BaseType = "[base type]";
                    }
                }

                // Declaring type (for nested types)
                var declaringHandle = typeDef.GetDeclaringType();
                if (!declaringHandle.IsNil)
                {
                    var declaring = md.GetTypeDefinition(declaringHandle);
                    detail.DeclaringType = FormatTypeName(md.GetString(declaring.Namespace), md.GetString(declaring.Name));
                }

                // Implemented interfaces
                foreach (var iih in typeDef.GetInterfaceImplementations())
                {
                    var impl = md.GetInterfaceImplementation(iih);
                    if (impl.Interface.IsNil) continue;
                    try { detail.Interfaces.Add(GetTypeName(md, impl.Interface)); } catch { }
                }

                // Nested types
                foreach (var nth in md.TypeDefinitions)
                {
                    var ntd = md.GetTypeDefinition(nth);
                    if (ntd.GetDeclaringType() == typeHandle)
                        detail.NestedTypes.Add(md.GetString(ntd.Name));
                }

                // Methods
                foreach (var mh in typeDef.GetMethods())
                    detail.Methods.Add(BuildMethodDetail(md, md.GetMethodDefinition(mh)));

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
                    methods.Add(BuildMethodDetail(md, md.GetMethodDefinition(mh)));

                return AnalysisResult<List<MethodDetail>>.Ok(methods, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                return AnalysisResult<List<MethodDetail>>.Fail($"List methods failed: {ex.Message}", sw.ElapsedMilliseconds);
            }
        }

        internal static TypeDefinitionHandle FindTypeHandle(MetadataReader md, string fullName)
        {
            // Accept both "Ns.Outer.Nested" and ILSpy-style "Ns.Outer/Nested" for nested types.
            var normalized = fullName.Replace('/', '.').Trim();
            foreach (var tdh in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(tdh);
                if (GetDottedFullTypeName(md, td).Equals(normalized, StringComparison.OrdinalIgnoreCase))
                    return tdh;
            }
            return default;
        }

        internal static MethodDefinitionHandle FindMethodHandle(MetadataReader md, TypeDefinitionHandle typeHandle, string methodName)
            => FindMethodHandles(md, typeHandle, methodName).FirstOrDefault();

        /// <summary>
        /// All methods of the type matching <paramref name="methodName"/> by name.
        /// Overload groups are returned together so callers can disambiguate by signature.
        /// </summary>
        internal static List<MethodDefinitionHandle> FindMethodHandles(MetadataReader md, TypeDefinitionHandle typeHandle, string methodName)
        {
            var results = new List<MethodDefinitionHandle>();
            var td = md.GetTypeDefinition(typeHandle);
            foreach (var mh in td.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                if (md.GetString(m.Name).Equals(methodName, StringComparison.OrdinalIgnoreCase))
                    results.Add(mh);
            }
            return results;
        }

        /// <summary>ILSpy-style full type name: "Ns.Type", or "Ns.Outer/Nested" with a slash per declaring level.</summary>
        internal static string GetILSpyFullTypeName(MetadataReader md, TypeDefinition td)
        {
            var declaring = td.GetDeclaringType();
            var name = md.GetString(td.Name);
            if (!declaring.IsNil)
                return GetILSpyFullTypeName(md, md.GetTypeDefinition(declaring)) + "/" + name;
            var ns = md.GetString(td.Namespace);
            return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        }

        /// <summary>Dotted variant ("Ns.Outer.Nested") — the form accepted by get-type / decompile-method / list-types.</summary>
        internal static string GetDottedFullTypeName(MetadataReader md, TypeDefinition td) =>
            GetILSpyFullTypeName(md, td).Replace('/', '.');

        private static (TypeDefinitionHandle, TypeDefinition) FindType(MetadataReader md, string fullName)
        {
            var handle = FindTypeHandle(md, fullName);
            if (handle.IsNil) return (default, default);
            return (handle, md.GetTypeDefinition(handle));
        }

        #region Helpers

        private static bool IsNested(TypeDefinition td) =>
            (td.Attributes & TypeAttributes.VisibilityMask) >= TypeAttributes.NestedPublic;

        private static string GetTypeName(MetadataReader md, EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition:
                    var td = md.GetTypeDefinition((TypeDefinitionHandle)handle);
                    return FormatTypeName(md.GetString(td.Namespace), md.GetString(td.Name));
                case HandleKind.TypeReference:
                    var tr = md.GetTypeReference((TypeReferenceHandle)handle);
                    return FormatTypeName(md.GetString(tr.Namespace), md.GetString(tr.Name));
                case HandleKind.TypeSpecification:
                    var ts = md.GetTypeSpecification((TypeSpecificationHandle)handle);
                    return ts.DecodeSignature(new DisassemblingSignatureTypeProvider(), default);
                default:
                    return "[unknown]";
            }
        }

        private static string FormatTypeName(string ns, string name) =>
            string.IsNullOrEmpty(ns) ? name : ns + "." + name;

        private static string GetRuntimeVersion(PEReader peReader)
        {
            try
            {
                var span = peReader.GetMetadata().GetContent().AsSpan();
                if (span.Length < 16) return "";
                var length = BitConverter.ToInt32(span.Slice(12, 4));
                if (length <= 0 || span.Length < 16 + length) return "";
                var version = System.Text.Encoding.UTF8.GetString(span.Slice(16, length));
                var nul = version.IndexOf('\0');
                if (nul >= 0) version = version.Substring(0, nul);
                return version.Trim();
            }
            catch
            {
                return "";
            }
        }

        internal static MethodDetail BuildMethodDetail(MetadataReader md, MethodDefinition m)
        {
            var name = md.GetString(m.Name);
            var sig = m.DecodeSignature(new DisassemblingSignatureTypeProvider(), default);

            var parameters = new List<ParameterDetail>();
            foreach (var ph in m.GetParameters())
            {
                var p = md.GetParameter(ph);
                var type = p.SequenceNumber > 0 && p.SequenceNumber <= sig.ParameterTypes.Length
                    ? sig.ParameterTypes[p.SequenceNumber - 1]
                    : "";
                var pName = md.GetString(p.Name);
                var isByRef = type.StartsWith("ref ", StringComparison.Ordinal);
                parameters.Add(new ParameterDetail
                {
                    Name = pName,
                    Type = type,
                    IsOut = (p.Attributes & ParameterAttributes.Out) != 0,
                    IsRef = isByRef && (p.Attributes & ParameterAttributes.Out) == 0,
                });
            }

            var genericParameters = new List<string>();
            foreach (var gph in m.GetGenericParameters())
                genericParameters.Add(md.GetString(md.GetGenericParameter(gph).Name));

            var attributes = new List<string>();
            foreach (var cah in m.GetCustomAttributes())
            {
                var attrName = GetCustomAttributeName(md, cah);
                if (attrName != null) attributes.Add(attrName);
            }

            var signature = $"{sig.ReturnType} {name}({string.Join(", ", sig.ParameterTypes)})";
            return new MethodDetail
            {
                Name = name,
                FullSignature = signature,
                ReturnType = sig.ReturnType,
                Parameters = parameters,
                GenericParameters = genericParameters,
                AccessLevel = GetMethodAccess(m),
                IsStatic = (m.Attributes & MethodAttributes.Static) != 0,
                IsVirtual = (m.Attributes & MethodAttributes.Virtual) != 0,
                IsAbstract = (m.Attributes & MethodAttributes.Abstract) != 0,
                IsOverride = (m.Attributes & MethodAttributes.Virtual) != 0 &&
                             (m.Attributes & MethodAttributes.NewSlot) == 0,
                IsSealed = (m.Attributes & MethodAttributes.Final) != 0,
                IsConstructor = name == ".ctor" || name == ".cctor",
                IsGetter = name.StartsWith("get_"),
                IsSetter = name.StartsWith("set_"),
                Attributes = attributes,
            };
        }

        /// <summary>
        /// Name of a custom attribute's type, or null when the constructor shape is not
        /// one of the common resolvable forms (TypeRef/TypeDef parent, MethodDef ctor).
        /// </summary>
        internal static string? GetCustomAttributeName(MetadataReader md, CustomAttributeHandle handle)
        {
            try
            {
                var ca = md.GetCustomAttribute(handle);
                switch (ca.Constructor.Kind)
                {
                    case HandleKind.MemberReference:
                        var mr = md.GetMemberReference((MemberReferenceHandle)ca.Constructor);
                        switch (mr.Parent.Kind)
                        {
                            case HandleKind.TypeReference:
                                var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
                                return FormatTypeName(md.GetString(tr.Namespace), md.GetString(tr.Name));
                            case HandleKind.TypeDefinition:
                                return GetDottedFullTypeName(md, md.GetTypeDefinition((TypeDefinitionHandle)mr.Parent));
                            default:
                                return null;
                        }
                    case HandleKind.MethodDefinition:
                        var ctor = md.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor);
                        var ownerHandle = ctor.GetDeclaringType();
                        if (ownerHandle.IsNil) return null;
                        return GetDottedFullTypeName(md, md.GetTypeDefinition(ownerHandle));
                    default:
                        return null;
                }
            }
            catch
            {
                return null;
            }
        }

        private static string GetTypeKind(MetadataReader md, TypeDefinition td)
        {
            if ((td.Attributes & TypeAttributes.Interface) != 0) return "Interface";
            if (td.BaseType.IsNil) return "Class";
            return GetTypeName(md, td.BaseType) switch
            {
                "System.Enum" => "Enum",
                "System.ValueType" => "Struct",
                "System.MulticastDelegate" => "Delegate",
                _ => "Class"
            };
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

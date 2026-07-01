using System.Collections.Generic;

namespace DnSpy.Analyzer.Core.Models
{
    public class AnalysisResult<T>
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public T? Data { get; set; }
        public long ElapsedMs { get; set; }

        public static AnalysisResult<T> Ok(T data, long elapsedMs) =>
            new AnalysisResult<T> { Success = true, Data = data, ElapsedMs = elapsedMs };
        public static AnalysisResult<T> Fail(string error, long elapsedMs) =>
            new AnalysisResult<T> { Success = false, Error = error, ElapsedMs = elapsedMs };
    }

    public class AssemblySummary
    {
        public string FilePath { get; set; } = "";
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string? Culture { get; set; }
        public string? PublicKeyToken { get; set; }
        public string Architecture { get; set; } = "";
        public string RuntimeVersion { get; set; } = "";
        public bool IsManaged { get; set; }
        public bool IsDotNet { get; set; }
        public long FileSize { get; set; }
    }

    public class AssemblyDependency
    {
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
    }

    public class NamespaceGroup
    {
        public string Namespace { get; set; } = "";
        public int TypeCount { get; set; }
    }

    public class TypeBrief
    {
        public string FullName { get; set; } = "";
        public string Name { get; set; } = "";
        public string Namespace { get; set; } = "";
        public string Kind { get; set; } = "";
        public string AccessLevel { get; set; } = "";
        public int MethodCount { get; set; }
        public int FieldCount { get; set; }
        public int PropertyCount { get; set; }
        public int EventCount { get; set; }
        public bool IsNested { get; set; }
        public string? DeclaringType { get; set; }
    }

    public class MemberBrief
    {
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public string AccessLevel { get; set; } = "";
        public string Signature { get; set; } = "";
    }

    public class MethodDetail
    {
        public string Name { get; set; } = "";
        public string FullSignature { get; set; } = "";
        public string ReturnType { get; set; } = "";
        public List<ParameterInfo> Parameters { get; set; } = new();
        public List<string> GenericParameters { get; set; } = new();
        public string AccessLevel { get; set; } = "";
        public bool IsStatic { get; set; }
        public bool IsVirtual { get; set; }
        public bool IsAbstract { get; set; }
        public bool IsOverride { get; set; }
        public bool IsSealed { get; set; }
        public bool IsConstructor { get; set; }
        public bool IsGetter { get; set; }
        public bool IsSetter { get; set; }
        public List<string> Attributes { get; set; } = new();
    }

    public class ParameterInfo
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public bool IsOut { get; set; }
        public bool IsRef { get; set; }
    }

    public class TypeDetail
    {
        public string FullName { get; set; } = "";
        public string Kind { get; set; } = "";
        public string AccessLevel { get; set; } = "";
        public string? BaseType { get; set; }
        public List<string> Interfaces { get; set; } = new();
        public List<string> NestedTypes { get; set; } = new();
        public string? DeclaringType { get; set; }
        public List<string> GenericParameters { get; set; } = new();
        public List<string> Attributes { get; set; } = new();
        public List<MethodDetail> Methods { get; set; } = new();
        public List<MemberBrief> Fields { get; set; } = new();
        public List<MemberBrief> Properties { get; set; } = new();
        public List<MemberBrief> Events { get; set; } = new();
        public bool IsSealed { get; set; }
        public bool IsAbstract { get; set; }
        public bool IsStatic { get; set; }
    }

    public class AssemblyDetail
    {
        public AssemblySummary Assembly { get; set; } = new();
        public List<AssemblyDependency> Dependencies { get; set; } = new();
        public List<NamespaceGroup> Namespaces { get; set; } = new();
        public int TotalTypes { get; set; }
    }

    public class SearchResult
    {
        public string Kind { get; set; } = "";
        public string FullName { get; set; } = "";
        public string? ParentType { get; set; }
        public string? Context { get; set; }
    }

    public class FolderScanResult
    {
        public string FolderPath { get; set; } = "";
        public List<AssemblySummary> Assemblies { get; set; } = new();
        public List<string> SkippedFiles { get; set; } = new();
    }

    public class DecompileResult
    {
        public string CSharpCode { get; set; } = "";
        public string? ILCode { get; set; }
        public string MethodName { get; set; } = "";
        public string TypeName { get; set; } = "";
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using DnSpy.Analyzer.Core;
using DnSpy.Analyzer.Core.Models;

namespace DnSpy.Analyzer.Cli
{
    /// <summary>
    /// dnSpy Analyzer CLI — analyze .NET DLL/EXE files from the command line.
    ///
    /// Usage:
    ///   analyzer scan-folder <path> [--no-recursive]
    ///   analyzer analyze-assembly <path>
    ///   analyzer list-types <path> [--namespace <ns>] [--offset <n>] [--limit <n>]
    ///   analyzer get-type <path> <type-name>
    ///   analyzer get-methods <path> <type-name>
    ///   analyzer decompile-method <path> <type-name> <method-name> [--signature <text>] [--reference-path <dir>]...
    ///   analyzer decompile-type <path> <type-name> [--reference-path <dir>]...
    ///   analyzer check-references <path> [--reference-path <dir>]...
    ///   analyzer search <path> <query> [--kind <kind>] [--max-results <n>]
    ///   analyzer help
    /// </summary>
    class Program
    {
        static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            // Keep non-ASCII (Chinese identifiers/strings) and plain punctuation readable
            // instead of \uXXXX-escaping everything; output is a terminal/pipe, not HTML.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>Exit code for the current invocation: 0 success, 1 any failure.</summary>
        static int _exitCode;

        static int Main(string[] args)
        {
            // Decompiled code and metadata names are full of non-ASCII; make piped
            // output UTF-8 instead of the legacy console code page.
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* redirected stream */ }

            if (args.Length == 0 || args[0] == "help" || args[0] == "--help" || args[0] == "-h")
            {
                PrintHelp();
                return 0;
            }

            var command = args[0];
            var rest = args[1..];

            try
            {
                var json = command switch
                {
                    "scan-folder" => HandleScanFolder(rest),
                    "analyze-assembly" => HandleAnalyzeAssembly(rest),
                    "list-types" => HandleListTypes(rest),
                    "get-type" => HandleGetType(rest),
                    "get-methods" => HandleGetMethods(rest),
                    "decompile-method" => HandleDecompileMethod(rest),
                    "decompile-type" => HandleDecompileType(rest),
                    "check-references" => HandleCheckReferences(rest),
                    "search" => HandleSearch(rest),
                    _ => Error($"Unknown command: {command}")
                };

                Console.Out.WriteLine(json);
                return _exitCode;
            }
            catch (Exception ex)
            {
                var err = Json(AnalysisResult<object>.Fail(ex.Message, 0));
                Console.Error.WriteLine(err);
                return 1;
            }
        }

        // ========== Handlers ==========

        static string HandleScanFolder(string[] args)
        {
            if (args.Length == 0) return Error("Usage: analyzer scan-folder <path> [--no-recursive]");
            var path = args[0];
            var recursive = true;
            for (int i = 1; i < args.Length; i++)
                if (args[i] == "--no-recursive") recursive = false;

            return Json(new AssemblyAnalyzer().ScanFolder(path, recursive));
        }

        static string HandleAnalyzeAssembly(string[] args)
        {
            if (args.Length == 0) return Error("Usage: analyzer analyze-assembly <path>");
            return Json(new AssemblyAnalyzer().AnalyzeAssembly(args[0]));
        }

        static string HandleListTypes(string[] args)
        {
            if (args.Length == 0) return Error("Usage: analyzer list-types <path> [--namespace <ns>] [--offset <n>] [--limit <n>]");
            var path = args[0];
            string? ns = null;
            int offset = 0, limit = 200;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--namespace" && i + 1 < args.Length) ns = args[++i];
                else if (args[i] == "--offset" && i + 1 < args.Length) int.TryParse(args[++i], out offset);
                else if (args[i] == "--limit" && i + 1 < args.Length) int.TryParse(args[++i], out limit);
            }
            return Json(new AssemblyAnalyzer().ListTypes(path, ns, offset, limit));
        }

        static string HandleGetType(string[] args)
        {
            if (args.Length < 2) return Error("Usage: analyzer get-type <path> <type-name>");
            return Json(new AssemblyAnalyzer().GetTypeDetail(args[0], args[1]));
        }

        static string HandleGetMethods(string[] args)
        {
            if (args.Length < 2) return Error("Usage: analyzer get-methods <path> <type-name>");
            return Json(new AssemblyAnalyzer().ListMethods(args[0], args[1]));
        }

        static string HandleDecompileMethod(string[] args)
        {
            if (args.Length < 3) return Error("Usage: analyzer decompile-method <path> <type-name> <method-name> [--signature <text>] [--reference-path <dir>]...");
            var (path, typeName, methodName, refPaths, signature) = ParseDecompileArgs(args);
            using var decompiler = new DecompilationHelper(refPaths);
            return Json(decompiler.DecompileMethod(path, typeName, methodName, signature));
        }

        static string HandleDecompileType(string[] args)
        {
            if (args.Length < 2) return Error("Usage: analyzer decompile-type <path> <type-name> [--reference-path <dir>]...");
            var (path, typeName, _, refPaths, _) = ParseDecompileArgs(args);
            using var decompiler = new DecompilationHelper(refPaths);
            return Json(decompiler.DecompileType(path, typeName));
        }

        static string HandleCheckReferences(string[] args)
        {
            if (args.Length < 1) return Error("Usage: analyzer check-references <path> [--reference-path <dir>]...");
            var path = args[0];
            var refPaths = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--reference-path" && i + 1 < args.Length)
                    refPaths.Add(args[++i]);
            }
            using var decompiler = new DecompilationHelper(refPaths);
            return Json(decompiler.CheckReferences(path));
        }

        /// <summary>
        /// Parses positional args for the decompile commands while collecting
        /// <c>--reference-path &lt;dir&gt;</c> and <c>--signature &lt;text&gt;</c> options
        /// from anywhere after the positionals.
        /// </summary>
        static (string Path, string TypeName, string MemberName, List<string> RefPaths, string? Signature) ParseDecompileArgs(string[] args)
        {
            var positional = new List<string>();
            var refPaths = new List<string>();
            string? signature = null;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--reference-path" && i + 1 < args.Length)
                {
                    refPaths.Add(args[++i]);
                }
                else if (args[i] == "--signature" && i + 1 < args.Length)
                {
                    signature = args[++i];
                }
                else
                {
                    positional.Add(args[i]);
                }
            }

            return (
                positional.Count > 0 ? positional[0] : "",
                positional.Count > 1 ? positional[1] : "",
                positional.Count > 2 ? positional[2] : "",
                refPaths,
                signature);
        }

        static string HandleSearch(string[] args)
        {
            if (args.Length < 2) return Error("Usage: analyzer search <path> <query> [--kind <kind>] [--max-results <n>]");
            var path = args[0];
            var query = args[1];
            var kind = SearchService.SearchKind.All;
            int maxResults = 100;
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--kind" && i + 1 < args.Length)
                {
                    kind = args[++i].ToLowerInvariant() switch
                    {
                        "type" => SearchService.SearchKind.Type,
                        "method" => SearchService.SearchKind.Method,
                        "field" => SearchService.SearchKind.Field,
                        "property" => SearchService.SearchKind.Property,
                        _ => SearchService.SearchKind.All
                    };
                }
                else if (args[i] == "--max-results" && i + 1 < args.Length)
                    int.TryParse(args[++i], out maxResults);
            }
            return Json(new SearchService().Search(path, query, kind, maxResults));
        }

        // ========== Helpers ==========

        static string Json<T>(AnalysisResult<T> result)
        {
            _exitCode = result.Success ? 0 : 1;
            return JsonSerializer.Serialize(result, JsonOpts);
        }

        static string Error(string msg)
        {
            _exitCode = 1;
            return JsonSerializer.Serialize(new AnalysisResult<object> { Success = false, Error = msg, ElapsedMs = 0 }, JsonOpts);
        }

        static void PrintHelp()
        {
            Console.Error.WriteLine(@"
dnSpy Analyzer CLI v1.0 — .NET assembly reverse engineering tool
Powered by dnSpy/ILSpy

USAGE:
  analyzer <command> [arguments...]

COMMANDS:
  scan-folder <path> [--no-recursive]
    Scan directory for .NET assemblies.

  analyze-assembly <path>
    Get assembly metadata (version, dependencies, namespaces).

  list-types <path> [--namespace <ns>] [--offset <n>] [--limit <n>]
    List types in an assembly, optionally filtered by namespace.

  get-type <path> <type-name>
    Get detailed information about a specific type.

  get-methods <path> <type-name>
    List all methods of a type with signatures.

  decompile-method <path> <type-name> <method-name> [--signature <text>] [--reference-path <dir>]...
    Decompile a method to C# source code. When <method-name> has several
    overloads the command fails and lists all candidate signatures; pass
    --signature to pick one (case-insensitive substring match on the rendered
    signature, e.g. --signature ""Int32"").

  decompile-type <path> <type-name> [--reference-path <dir>]...
    Decompile an entire type to C# source code.

  check-references <path> [--reference-path <dir>]...
    Show which referenced assemblies can be resolved, and from where.
    Use this first when decompilation fails with 'Failed to resolve assembly'.

  search <path> <query> [--kind <type|method|field|property>] [--max-results <n>]
    Search for types, methods, fields, or properties by name.

  help
    Show this help.

REFERENCE PATHS:
  Decompiling Unity / .NET Framework assemblies usually needs the sibling DLLs
  (UnityEngine.dll, mscorlib.dll, ...) to be discoverable. Pass one or more
  --reference-path options to add probe directories; repeat the option for
  multiple directories. The target assembly's own directory is always probed.

EXAMPLES:
  analyzer scan-folder ./game/Managed
  analyzer analyze-assembly ./game/Managed/Assembly-CSharp.dll
  analyzer list-types ./game/Managed/Assembly-CSharp.dll --namespace Game.Core
  analyzer get-type ./game/Managed/Assembly-CSharp.dll Game.Core.PlayerController
  analyzer check-references ./game/Managed/Assembly-CSharp.dll --reference-path ./game/Managed
  analyzer decompile-method ./game/Managed/Assembly-CSharp.dll Game.Core.PlayerController TakeDamage
  analyzer decompile-type ./game/Managed/Assembly-CSharp.dll GameSocket --reference-path ./game/Managed
  analyzer search ./game/Managed/Assembly-CSharp.dll health --kind field

OUTPUT:
  All results are JSON on stdout — including failures as {""success"":false,""error"":...}.
  Unexpected internal errors go to stderr as JSON.
  Exit code 0 = success, 1 = any failure (usage errors, unknown command, not found).
  Non-ASCII text (e.g. Chinese strings) is emitted as raw UTF-8, not \uXXXX escapes.

NESTED TYPES:
  Use the dotted form everywhere: Ns.Outer.Nested (list-types outputs this form in
  fullName; search results can be pasted directly). decompile-type also accepts the
  ILSpy form Ns.Outer/Nested.
");
        }
    }
}

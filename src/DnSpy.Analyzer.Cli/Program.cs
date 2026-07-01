using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
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
    ///   analyzer decompile-method <path> <type-name> <method-name>
    ///   analyzer decompile-type <path> <type-name>
    ///   analyzer search <path> <query> [--kind <kind>] [--max-results <n>]
    ///   analyzer help
    /// </summary>
    class Program
    {
        static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        static async Task<int> Main(string[] args)
        {
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
                    "analyze-assembly" => await HandleAnalyzeAssembly(rest),
                    "list-types" => await HandleListTypes(rest),
                    "get-type" => await HandleGetType(rest),
                    "get-methods" => await HandleGetMethods(rest),
                    "decompile-method" => await HandleDecompileMethod(rest),
                    "decompile-type" => await HandleDecompileType(rest),
                    "search" => await HandleSearch(rest),
                    _ => Json(AnalysisResult<object>.Fail($"Unknown command: {command}", 0))
                };

                Console.Out.WriteLine(json);
                return 0;
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

        static async Task<string> HandleAnalyzeAssembly(string[] args)
        {
            if (args.Length == 0) return Error("Usage: analyzer analyze-assembly <path>");
            var analyzer = new AssemblyAnalyzer();
            return await Task.Run(() => Json(analyzer.AnalyzeAssembly(args[0])));
        }

        static async Task<string> HandleListTypes(string[] args)
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
            var analyzer = new AssemblyAnalyzer();
            return await Task.Run(() => Json(analyzer.ListTypes(path, ns, offset, limit)));
        }

        static async Task<string> HandleGetType(string[] args)
        {
            if (args.Length < 2) return Error("Usage: analyzer get-type <path> <type-name>");
            var analyzer = new AssemblyAnalyzer();
            return await Task.Run(() => Json(analyzer.GetTypeDetail(args[0], args[1])));
        }

        static async Task<string> HandleGetMethods(string[] args)
        {
            if (args.Length < 2) return Error("Usage: analyzer get-methods <path> <type-name>");
            var analyzer = new AssemblyAnalyzer();
            return await Task.Run(() => Json(analyzer.ListMethods(args[0], args[1])));
        }

        static async Task<string> HandleDecompileMethod(string[] args)
        {
            if (args.Length < 3) return Error("Usage: analyzer decompile-method <path> <type-name> <method-name>");
            using var decompiler = new DecompilationHelper();
            return await Task.Run(() => Json(decompiler.DecompileMethod(args[0], args[1], args[2])));
        }

        static async Task<string> HandleDecompileType(string[] args)
        {
            if (args.Length < 2) return Error("Usage: analyzer decompile-type <path> <type-name>");
            using var decompiler = new DecompilationHelper();
            return await Task.Run(() => Json(decompiler.DecompileType(args[0], args[1])));
        }

        static async Task<string> HandleSearch(string[] args)
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
            var searcher = new SearchService();
            return await Task.Run(() => Json(searcher.Search(path, query, kind, maxResults)));
        }

        // ========== Helpers ==========

        static string Json<T>(AnalysisResult<T> result) =>
            JsonSerializer.Serialize(result, JsonOpts);

        static string Error(string msg) =>
            JsonSerializer.Serialize(new { success = false, error = msg, elapsedMs = 0 }, JsonOpts);

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

  decompile-method <path> <type-name> <method-name>
    Decompile a method to C# source code (requires ICSharpCode.Decompiler).

  decompile-type <path> <type-name>
    Decompile an entire type to C# source code.

  search <path> <query> [--kind <type|method|field|property>] [--max-results <n>]
    Search for types, methods, fields, or properties by name.

  help
    Show this help.

EXAMPLES:
  analyzer scan-folder ./game/Managed
  analyzer analyze-assembly ./game/Managed/Assembly-CSharp.dll
  analyzer list-types ./game/Managed/Assembly-CSharp.dll --namespace Game.Core
  analyzer get-type ./game/Managed/Assembly-CSharp.dll Game.Core.PlayerController
  analyzer decompile-method ./game/Managed/Assembly-CSharp.dll Game.Core.PlayerController TakeDamage
  analyzer search ./game/Managed/Assembly-CSharp.dll health --kind field

OUTPUT:
  All results are JSON written to stdout. Errors go to stderr.
  Exit code 0 = success, 1 = error.
");
        }
    }
}

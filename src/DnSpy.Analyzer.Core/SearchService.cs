using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DnSpy.Analyzer.Core.Models;

namespace DnSpy.Analyzer.Core
{
    public class SearchService
    {
        public enum SearchKind
        {
            All, Type, Method, Field, Property
        }

        public AnalysisResult<List<SearchResult>> Search(
            string path, string query, SearchKind kind = SearchKind.All, int maxResults = 100)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (!File.Exists(path))
                    return AnalysisResult<List<SearchResult>>.Fail($"File not found: {path}", sw.ElapsedMilliseconds);

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                using var peReader = new PEReader(fs, PEStreamOptions.PrefetchEntireImage);
                if (!peReader.HasMetadata)
                    return AnalysisResult<List<SearchResult>>.Fail("Not a .NET assembly", sw.ElapsedMilliseconds);

                var md = peReader.GetMetadataReader();
                var results = new List<SearchResult>();

                foreach (var tdh in md.TypeDefinitions)
                {
                    if (results.Count >= maxResults) break;
                    var td = md.GetTypeDefinition(tdh);
                    var ns = md.GetString(td.Namespace);
                    var name = md.GetString(td.Name);
                    var fullName = (string.IsNullOrEmpty(ns) ? "" : ns + ".") + name;

                    if (fullName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (kind == SearchKind.All || kind == SearchKind.Type)
                        {
                            results.Add(new SearchResult { Kind = "Type", FullName = fullName });
                            if (results.Count >= maxResults) break;
                        }
                    }

                    // Methods
                    foreach (var mh in td.GetMethods())
                    {
                        if (results.Count >= maxResults) break;
                        var m = md.GetMethodDefinition(mh);
                        var mName = md.GetString(m.Name);
                        if (mName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (kind == SearchKind.All || kind == SearchKind.Method)
                            {
                                results.Add(new SearchResult { Kind = "Method", FullName = fullName + "." + mName, ParentType = fullName });
                            }
                        }
                    }

                    // Fields
                    foreach (var fh in td.GetFields())
                    {
                        if (results.Count >= maxResults) break;
                        var f = md.GetFieldDefinition(fh);
                        var fName = md.GetString(f.Name);
                        if (fName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (kind == SearchKind.All || kind == SearchKind.Field)
                            {
                                results.Add(new SearchResult { Kind = "Field", FullName = fullName + "." + fName, ParentType = fullName });
                            }
                        }
                    }

                    // Properties
                    foreach (var ph in td.GetProperties())
                    {
                        if (results.Count >= maxResults) break;
                        var p = md.GetPropertyDefinition(ph);
                        var pName = md.GetString(p.Name);
                        if (pName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (kind == SearchKind.All || kind == SearchKind.Property)
                            {
                                results.Add(new SearchResult { Kind = "Property", FullName = fullName + "." + pName, ParentType = fullName });
                            }
                        }
                    }
                }

                return AnalysisResult<List<SearchResult>>.Ok(results, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                return AnalysisResult<List<SearchResult>>.Fail($"Search failed: {ex.Message}", sw.ElapsedMilliseconds);
            }
        }
    }
}

using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class SearchSymbolsLogic
{
    private const int MaxResults = 50;

    public static List<SymbolLocation> Execute(SymbolResolver resolver, string query)
    {
        var results = new List<SymbolLocation>();
        var lowerQuery = query.ToLowerInvariant();

        // Search types by simple name (substring match)
        foreach (var (simpleName, types) in resolver.TypesBySimpleName)
        {
            if (!simpleName.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var type in types)
            {
                var (file, line) = resolver.GetFileAndLine(type);
                if (string.IsNullOrEmpty(file))
                    continue;

                var kind = type.TypeKind switch
                {
                    TypeKind.Interface => "interface",
                    TypeKind.Struct => "struct",
                    TypeKind.Enum => "enum",
                    TypeKind.Delegate => "delegate",
                    _ => "class"
                };

                var project = resolver.GetProjectName(type);
                results.Add(new SymbolLocation(kind, type.ToDisplayString(), file, line, project));

                if (results.Count >= MaxResults)
                    return results;
            }
        }

        // Also search members if query looks like it could be a member name
        foreach (var type in resolver.AllTypes)
        {
            foreach (var member in type.GetMembers())
            {
                if (member.IsImplicitlyDeclared)
                    continue;

                if (!member.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;

                var (file, line) = resolver.GetFileAndLine(member);
                if (string.IsNullOrEmpty(file))
                    continue;

                string? kind = member switch
                {
                    IMethodSymbol m when m.MethodKind == MethodKind.Constructor => "constructor",
                    IMethodSymbol => "method",
                    IPropertySymbol => "property",
                    IFieldSymbol => "field",
                    IEventSymbol => "event",
                    _ => null
                };

                if (kind == null)
                    continue;

                var project = resolver.GetProjectName(member);
                results.Add(new SymbolLocation(kind, member.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), file, line, project));

                if (results.Count >= MaxResults)
                    return results;
            }
        }

        return results;
    }
}

[McpServerToolType]
public static class SearchSymbolsTool
{
    [McpServerTool(Name = "search_symbols"),
     Description("Search for types, methods, properties, and fields by name (case-insensitive substring match, max 50 results)")]
    public static List<SymbolLocation> Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        [Description("Search query (substring match against symbol names)")] string query)
    {
        SolutionGuard.EnsureLoaded(loaded);
        return SearchSymbolsLogic.Execute(resolver, query);
    }
}

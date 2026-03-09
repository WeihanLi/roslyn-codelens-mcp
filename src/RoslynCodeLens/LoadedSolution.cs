using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace RoslynCodeLens;

public class LoadedSolution
{
    public required Solution Solution { get; init; }
    public required ConcurrentDictionary<ProjectId, Compilation> Compilations { get; init; }
    public bool IsEmpty => Compilations.IsEmpty;

    public static LoadedSolution Empty { get; } = CreateEmpty();

    private static LoadedSolution CreateEmpty()
    {
        var workspace = new AdhocWorkspace();
        return new LoadedSolution
        {
            Solution = workspace.CurrentSolution,
            Compilations = new ConcurrentDictionary<ProjectId, Compilation>()
        };
    }
}

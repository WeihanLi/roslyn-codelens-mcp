using RoslynCodeLens;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class UnloadSolutionToolTests
{
    private readonly string _solutionPath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));

    [Fact]
    public async Task Execute_ValidName_ReturnsUnloadedMessage()
    {
        var manager = MultiSolutionManager.CreateEmpty();
        await manager.LoadSolutionAsync(_solutionPath);

        var result = UnloadSolutionTool.Execute(manager, "TestSolution");

        Assert.Contains("Unloaded", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TestSolution", result, StringComparison.OrdinalIgnoreCase);
        manager.Dispose();
    }

    [Fact]
    public void Execute_UnknownName_PropagatesException()
    {
        var manager = MultiSolutionManager.CreateEmpty();

        Assert.Throws<InvalidOperationException>(() => UnloadSolutionTool.Execute(manager, "DoesNotExist"));

        manager.Dispose();
    }
}

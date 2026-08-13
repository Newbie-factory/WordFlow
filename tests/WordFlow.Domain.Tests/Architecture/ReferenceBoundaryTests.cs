namespace WordFlow.Domain.Tests.Architecture;

public sealed class ReferenceBoundaryTests
{
    [Fact]
    public void Production_project_reference_graph_matches_the_allowed_architecture()
    {
        IReadOnlyDictionary<string, string[]> actual = ProjectReferenceGraph.ReadFromSolutionRoot(
            SolutionRoot.FindFromTestOutput());

        ProjectReferenceGraph.AssertMatchesAllowedArchitecture(actual);
    }

    [Fact]
    public void Project_reference_graph_rejects_an_unused_forbidden_outer_layer_reference()
    {
        IReadOnlyDictionary<string, string[]> graph = new Dictionary<string, string[]>
        {
            ["WordFlow.Domain"] = ["WordFlow.App"],
            ["WordFlow.Application"] = ["WordFlow.Domain"],
            ["WordFlow.Infrastructure"] = ["WordFlow.Application", "WordFlow.Domain"],
            ["WordFlow.App"] = ["WordFlow.Application", "WordFlow.Infrastructure"],
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ProjectReferenceGraph.AssertMatchesAllowedArchitecture(graph));

        Assert.Contains("WordFlow.Domain", exception.Message);
        Assert.Contains("WordFlow.App", exception.Message);
    }

    [Fact]
    public void Domain_does_not_reference_outer_layers()
    {
        string[] names = typeof(WordFlow.Domain.AssemblyMarker).Assembly
            .GetReferencedAssemblies().Select(x => x.Name ?? "").ToArray();
        Assert.DoesNotContain("WordFlow.Application", names);
        Assert.DoesNotContain("WordFlow.Infrastructure", names);
        Assert.DoesNotContain("WordFlow.App", names);
    }
}

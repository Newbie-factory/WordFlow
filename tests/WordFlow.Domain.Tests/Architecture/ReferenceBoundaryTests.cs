namespace WordFlow.Domain.Tests.Architecture;

public sealed class ReferenceBoundaryTests
{
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

using System.Xml.Linq;

namespace WordFlow.Domain.Tests.Architecture;

internal static class ProjectReferenceGraph
{
    private static readonly IReadOnlyDictionary<string, string[]> AllowedArchitecture =
        new Dictionary<string, string[]>
        {
            ["WordFlow.Domain"] = [],
            ["WordFlow.Application"] = ["WordFlow.Domain"],
            ["WordFlow.Infrastructure"] = ["WordFlow.Application", "WordFlow.Domain"],
            ["WordFlow.App"] = ["WordFlow.Application", "WordFlow.Infrastructure"],
        };

    internal static IReadOnlyDictionary<string, string[]> ReadFromSolutionRoot(string solutionRoot)
    {
        Dictionary<string, string[]> graph = [];

        foreach (string projectName in AllowedArchitecture.Keys)
        {
            string projectPath = Path.Combine(solutionRoot, "src", projectName, $"{projectName}.csproj");
            XDocument project = XDocument.Load(projectPath);
            string projectDirectory = Path.GetDirectoryName(projectPath)
                ?? throw new InvalidOperationException($"Project directory was not found for '{projectPath}'.");

            graph[projectName] = project
                .Descendants("ProjectReference")
                .Select(reference => reference.Attribute("Include")?.Value)
                .OfType<string>()
                .Where(include => !string.IsNullOrWhiteSpace(include))
                .Select(include => Path.GetFullPath(include, projectDirectory))
                .Select(path => Path.GetFileNameWithoutExtension(path)
                    ?? throw new InvalidOperationException($"Project name was not found for '{path}'."))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }

        return graph;
    }

    internal static void AssertMatchesAllowedArchitecture(IReadOnlyDictionary<string, string[]> actual)
    {
        foreach ((string project, string[] expectedReferences) in AllowedArchitecture)
        {
            if (!actual.TryGetValue(project, out string[]? actualReferences))
            {
                throw new InvalidOperationException($"Missing project '{project}' from the reference graph.");
            }

            string[] expected = expectedReferences.OrderBy(name => name, StringComparer.Ordinal).ToArray();
            if (!actualReferences.SequenceEqual(expected, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Project '{project}' references [{string.Join(", ", actualReferences)}], " +
                    $"but must reference [{string.Join(", ", expected)}].");
            }
        }

        string[] unexpectedProjects = actual.Keys.Except(AllowedArchitecture.Keys, StringComparer.Ordinal).ToArray();
        if (unexpectedProjects.Length > 0)
        {
            throw new InvalidOperationException(
                $"Unexpected projects in the reference graph: {string.Join(", ", unexpectedProjects)}.");
        }
    }
}

internal static class SolutionRoot
{
    internal static string FindFromTestOutput()
    {
        foreach (string startDirectory in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (DirectoryInfo? directory = new(startDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "WordFlow.sln")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new InvalidOperationException("Could not locate the WordFlow solution root from the test output.");
    }
}

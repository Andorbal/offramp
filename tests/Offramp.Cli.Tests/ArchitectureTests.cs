using System.Reflection;
using System.Xml.Linq;

namespace Offramp.Cli.Tests;

/// <summary>
/// The layering rule of CLAUDE.md and docs/spec/00-architecture.md: <c>Core</c> references nothing
/// in src; <c>Llm</c> and <c>Mcp</c> are leaves that only <c>Cli</c> references, so nothing that
/// decides what to move, which version to take, or whether code compiles can call a model.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly string[] Leaves = ["Offramp.Llm", "Offramp.Mcp"];

    [Fact]
    public void Project_references_follow_the_layering_rule()
    {
        var graph = ProjectGraph(RepositoryRoot());

        Assert.Contains("Offramp.Llm", graph["Offramp.Cli"]);
        Assert.Contains("Offramp.Mcp", graph["Offramp.Cli"]);
        Assert.Empty(Violations(graph));
    }

    [Fact]
    public void Compiled_assemblies_follow_it_too()
    {
        // Catches a reference that bypasses ProjectReference (a HintPath, a copied DLL).
        foreach (var assembly in new[] { "Offramp.Core", "Offramp.Workspace", "Offramp.NuGet", "Offramp.Analysis", "Offramp.Refactoring", "Offramp.Scaffolding", "Offramp.Reporting" })
        {
            var references = Assembly.Load(assembly).GetReferencedAssemblies().Select(a => a.Name).ToList();
            Assert.DoesNotContain("Offramp.Llm", references);
            Assert.DoesNotContain("Offramp.Mcp", references);
            Assert.DoesNotContain("Offramp.Cli", references);
        }

        Assert.DoesNotContain(Assembly.Load("Offramp.Core").GetReferencedAssemblies(), a => a.Name!.StartsWith("Offramp.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_violating_graph_is_caught()
    {
        var graph = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["Offramp.Cli"] = new HashSet<string> { "Offramp.Analysis", "Offramp.Llm" },
            ["Offramp.Analysis"] = new HashSet<string> { "Offramp.Workspace" },
            ["Offramp.Workspace"] = new HashSet<string> { "Offramp.Core", "Offramp.Llm" },
            ["Offramp.Core"] = new HashSet<string> { "Offramp.Mcp" },
            ["Offramp.Llm"] = new HashSet<string> { "Offramp.Core" },
            ["Offramp.Mcp"] = new HashSet<string>(),
        };

        Assert.Equal(
        [
            "Offramp.Analysis reaches Offramp.Llm, which only Offramp.Cli may reference",
            "Offramp.Analysis reaches Offramp.Mcp, which only Offramp.Cli may reference",
            "Offramp.Core reaches Offramp.Mcp, which only Offramp.Cli may reference",
            "Offramp.Core references Offramp.Mcp; Core references nothing in src",
            "Offramp.Llm references Offramp.Core; leaves reference nothing in src",
            "Offramp.Workspace reaches Offramp.Llm, which only Offramp.Cli may reference",
            "Offramp.Workspace reaches Offramp.Mcp, which only Offramp.Cli may reference",
        ], Violations(graph));
    }

    /// <summary>Every broken rule, sorted.</summary>
    internal static List<string> Violations(IReadOnlyDictionary<string, IReadOnlySet<string>> graph)
    {
        var violations = new List<string>();
        foreach (var (project, references) in graph)
        {
            if (project == "Offramp.Core" || Leaves.Contains(project))
            {
                violations.AddRange(references.Select(r => project == "Offramp.Core"
                    ? $"{project} references {r}; Core references nothing in src"
                    : $"{project} references {r}; leaves reference nothing in src"));
            }

            if (project == "Offramp.Cli" || Leaves.Contains(project))
            {
                continue;
            }

            foreach (var leaf in Leaves.Where(Reach(graph, project).Contains))
            {
                violations.Add($"{project} reaches {leaf}, which only Offramp.Cli may reference");
            }
        }

        return [.. violations.Order(StringComparer.Ordinal)];
    }

    private static HashSet<string> Reach(IReadOnlyDictionary<string, IReadOnlySet<string>> graph, string project)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(graph.GetValueOrDefault(project) ?? new HashSet<string>());
        while (pending.TryPop(out var next))
        {
            if (seen.Add(next))
            {
                foreach (var further in graph.GetValueOrDefault(next) ?? new HashSet<string>())
                {
                    pending.Push(further);
                }
            }
        }

        return seen;
    }

    /// <summary>src/*/*.csproj and their ProjectReferences, by project name.</summary>
    private static Dictionary<string, IReadOnlySet<string>> ProjectGraph(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToDictionary(
                p => Path.GetFileNameWithoutExtension(p),
                p => (IReadOnlySet<string>)XDocument.Load(p).Descendants("ProjectReference")
                    .Select(r => Path.GetFileNameWithoutExtension(r.Attribute("Include")!.Value.Replace('\\', '/')))
                    .ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Offramp.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (Offramp.slnx) is not above the test's directory.");
    }
}

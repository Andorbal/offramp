using System.Xml.Linq;
using Offramp.Fixtures;

namespace Offramp.Core.Tests;

/// <summary>
/// The dependency direction from CLAUDE.md and docs/spec/00-architecture.md:
/// Core references nothing in src/, libraries point toward Core, Llm and Mcp
/// are leaves, and no core library reaches Offramp.Llm even transitively.
/// </summary>
public sealed class LayeringTests
{
    /// <summary>Every src project and the src projects it may reference directly.</summary>
    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        ["Offramp.Core"] = [],
        ["Offramp.Workspace"] = ["Offramp.Core"],
        ["Offramp.Analysis"] = ["Offramp.Core", "Offramp.Workspace"],
        ["Offramp.NuGet"] = ["Offramp.Core", "Offramp.Workspace", "Offramp.Analysis"],
        ["Offramp.Refactoring"] = ["Offramp.Core", "Offramp.Workspace", "Offramp.Analysis", "Offramp.NuGet"],
        ["Offramp.Scaffolding"] = ["Offramp.Core", "Offramp.Workspace", "Offramp.Analysis", "Offramp.NuGet", "Offramp.Refactoring"],
        ["Offramp.Reporting"] = ["Offramp.Core", "Offramp.Workspace", "Offramp.Analysis", "Offramp.NuGet"],
        ["Offramp.Analyzers"] = [],
        ["Offramp.Llm"] = ["Offramp.Core"],
        ["Offramp.Mcp"] = ["Offramp.Core"],
        ["Offramp.Cli"] =
        [
            "Offramp.Core", "Offramp.Workspace", "Offramp.Analysis", "Offramp.NuGet", "Offramp.Refactoring",
            "Offramp.Scaffolding", "Offramp.Reporting", "Offramp.Analyzers", "Offramp.Llm", "Offramp.Mcp",
        ],
    };

    private static readonly string[] MustNotReachLlm =
        ["Offramp.Core", "Offramp.Workspace", "Offramp.NuGet", "Offramp.Analysis", "Offramp.Refactoring"];

    private static Dictionary<string, List<string>> References()
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var csproj in Directory.EnumerateFiles(RepositoryFiles.Path("src"), "*.csproj", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(csproj);
            result[name] = [.. XDocument.Load(csproj).Descendants("ProjectReference")
                .Select(r => Path.GetFileNameWithoutExtension(r.Attribute("Include")!.Value.Replace('\\', '/')))
                .Order(StringComparer.Ordinal)];
        }

        return result;
    }

    [Fact]
    public void Every_project_references_only_what_the_layering_allows()
    {
        foreach (var (project, references) in References())
        {
            Assert.True(Allowed.ContainsKey(project),
                $"{project} is not in the layering table; add it deliberately (see CLAUDE.md, repository layout).");
            var forbidden = references.Except(Allowed[project]).ToList();
            Assert.True(forbidden.Count == 0, $"{project} must not reference {string.Join(", ", forbidden)}.");
        }
    }

    [Fact]
    public void Nothing_but_the_cli_references_the_llm_or_mcp_leaves()
    {
        foreach (var (project, references) in References().Where(p => p.Key != "Offramp.Cli"))
        {
            Assert.DoesNotContain("Offramp.Llm", references);
            Assert.DoesNotContain("Offramp.Mcp", references);
            Assert.DoesNotContain("Offramp.Cli", references);
        }
    }

    [Fact]
    public void Core_libraries_do_not_reach_the_llm_transitively()
    {
        var graph = References();
        foreach (var start in MustNotReachLlm.Where(graph.ContainsKey))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>([start]);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!seen.Add(current) || !graph.TryGetValue(current, out var next))
                {
                    continue;
                }

                foreach (var reference in next)
                {
                    pending.Push(reference);
                }
            }

            Assert.DoesNotContain("Offramp.Llm", seen);
        }
    }
}

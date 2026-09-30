using System.Text;
using Offramp.Core.Diagnostics;
using Offramp.Fixtures;
using Offramp.Refactoring.ProjectFiles;

namespace Offramp.Refactoring.Tests;

/// <summary>Adding a project to a <c>.sln</c> changes nothing else in it.</summary>
public sealed class SolutionEditorTests
{
    [Fact]
    public async Task A_project_is_added_to_a_sln_without_rewriting_the_rest()
    {
        // As in NHibernate 4.1.2: a Visual Studio 2010 solution (Format Version 11.00, CRLF) with a
        // TestCaseManagementSettings section, which the serializer's rewrite changed and dropped.
        using var root = new ScratchDirectory("sln");
        byte[] before = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(MoveExtractorTests.SolutionText)];
        File.WriteAllBytes(root.Combine("Signed.sln"), before);
        var diagnostics = new DiagnosticBag();

        var (path, original, after) = Assert.Single(await SolutionEditor.AddProjectAsync(root.Path, "Signed.sln", "src/Signed.Core/Signed.Core.csproj", CancellationToken.None, diagnostics));

        Assert.Equal("Signed.sln", path);
        Assert.Equal(before, original);
        Assert.Equal(before[..3], after[..3]);
        var lines = Encoding.UTF8.GetString(after, 3, after.Length - 3).Split("\r\n");
        var added = Added(lines, MoveExtractorTests.SolutionText.Split("\r\n"));
        Assert.Equal(6, added.Count);
        Assert.Matches("^Project\\(\"\\{[0-9A-F-]+\\}\"\\) = \"Signed.Core\", \"src\\\\Signed.Core\\\\Signed.Core.csproj\", \"\\{[0-9A-F-]+\\}\"$", added[0]);
        Assert.Equal("EndProject", added[1]);
        var guid = added[0][added[0].LastIndexOf('{')..].Trim('"');
        Assert.Equal(
            [$"\t\t{guid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU", $"\t\t{guid}.Debug|Any CPU.Build.0 = Debug|Any CPU", $"\t\t{guid}.Release|Any CPU.ActiveCfg = Release|Any CPU", $"\t\t{guid}.Release|Any CPU.Build.0 = Release|Any CPU"],
            added[2..]);

        // The project goes after the last one, the configurations at the end of their section.
        Assert.Equal(Array.IndexOf(lines, "EndProject") + 1, Array.IndexOf(lines, added[0]));
        Assert.Equal(Array.IndexOf(lines, "\t\t{5909BFE7-93CF-4E5F-BE22-6293368AF01D}.Release|Any CPU.Build.0 = Release|Any CPU") + 1, Array.IndexOf(lines, added[2]));
        Assert.Empty(diagnostics.ToSortedList());

        // Deterministic: the same edit again gives the same bytes.
        var again = Assert.Single(await SolutionEditor.AddProjectAsync(root.Path, "Signed.sln", "src/Signed.Core/Signed.Core.csproj", CancellationToken.None));
        Assert.Equal(after, again.After);
    }

    [Fact]
    public async Task A_project_joins_its_siblings_solution_folder()
    {
        using var root = new ScratchDirectory("sln");
        var text = MoveExtractorTests.SolutionText
            .Replace("EndProject\r\nGlobal\r\n",
                "EndProject\r\nProject(\"{2150E333-8FDC-42A3-9474-1A3956D46DE8}\") = \"src\", \"src\", \"{593DCEA7-C933-46F3-939F-D8172399AB05}\"\r\nEndProject\r\nGlobal\r\n", StringComparison.Ordinal)
            .Replace("EndGlobal\r\n",
                "\tGlobalSection(NestedProjects) = preSolution\r\n\t\t{5909BFE7-93CF-4E5F-BE22-6293368AF01D} = {593DCEA7-C933-46F3-939F-D8172399AB05}\r\n\tEndGlobalSection\r\nEndGlobal\r\n", StringComparison.Ordinal);
        root.Write("All.sln", text);

        var after = Encoding.UTF8.GetString(Assert.Single(await SolutionEditor.AddProjectAsync(root.Path, "All.sln", "src/Signed.Core/Signed.Core.csproj", CancellationToken.None)).After);

        var lines = after.Split("\r\n");
        var added = Added(lines, text.Split("\r\n"));
        Assert.Equal(7, added.Count);
        Assert.Matches("^\t\t\\{[0-9A-F-]+\\} = \\{593DCEA7-C933-46F3-939F-D8172399AB05\\}$", added[^1]);
    }

    /// <summary>The lines of <paramref name="after"/> that are not <paramref name="before"/>'s, which must all be there, in order.</summary>
    private static List<string> Added(string[] after, string[] before)
    {
        var added = new List<string>();
        var next = 0;
        foreach (var line in after)
        {
            if (next < before.Length && line == before[next])
            {
                next++;
            }
            else
            {
                added.Add(line);
            }
        }

        Assert.Equal(before.Length, next);
        return added;
    }

    [Fact]
    [ProducesDiagnostic("OFR2115")]
    public async Task A_sln_that_cannot_be_edited_in_place_is_rewritten_and_the_lost_lines_named()
    {
        // No Global section to put the project's configurations in.
        using var root = new ScratchDirectory("sln");
        var text = MoveExtractorTests.SolutionText[..(MoveExtractorTests.SolutionText.IndexOf("Global\r\n", StringComparison.Ordinal))];
        root.Write("Bare.sln", text);
        var diagnostics = new DiagnosticBag();

        var after = Encoding.UTF8.GetString(Assert.Single(await SolutionEditor.AddProjectAsync(root.Path, "Bare.sln", "src/Signed.Core/Signed.Core.csproj", CancellationToken.None, diagnostics)).After);

        Assert.Contains("src\\Signed.Core\\Signed.Core.csproj", after, StringComparison.Ordinal);
        var rewrite = Assert.Single(diagnostics.ToSortedList());
        Assert.Equal("OFR2115", rewrite.Code);
        Assert.Contains("\"Microsoft Visual Studio Solution File, Format Version 11.00\"", rewrite.Message, StringComparison.Ordinal);
    }
}

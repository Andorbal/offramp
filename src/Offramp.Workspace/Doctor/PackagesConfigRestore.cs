using System.Text;

namespace Offramp.Workspace.Doctor;

/// <summary>A file <c>doctor --fix</c> creates or changes besides Directory.Build.props and the project files' conditions.</summary>
public sealed record FileFix
{
    /// <summary>Repository-relative path.</summary>
    public required string File { get; init; }

    /// <summary>True when the file was written in this run.</summary>
    public required bool Applied { get; init; }

    /// <summary>The unified diff of the change (from <c>/dev/null</c> for a new file).</summary>
    public string? Diff { get; init; }
}

/// <summary>
/// <c>packages.config</c> for <c>dotnet restore</c>, so that a plain <c>dotnet build</c> of a legacy solution works on a
/// fresh clone outside Windows, without Offramp (docs/decisions/0064-dotnet-build-and-packages-config-without-offramp.md).
/// <c>dotnet restore</c> does not read <c>packages.config</c>, and <c>NuGet.exe</c> needs Mono, so the repository gets
/// <see cref="TargetsFileName"/>: after a restore it has NuGet's own restore download what each <c>packages.config</c>
/// lists and the packages folder lacks (through a generated SDK-style project, so the solution's feeds and credentials
/// apply), then lays the packages out as <c>nuget restore</c> does. Projects import it from the block's packages.config
/// section, and a solution's restore from <see cref="SolutionTargetsFileName"/>, which MSBuild imports into the
/// solution's metaproject. Visual Studio's MSBuild imports neither.
/// </summary>
public static class PackagesConfigRestore
{
    public const string TargetsFileName = "Offramp.PackagesConfig.targets";

    public const string SolutionTargetsFileName = "Directory.Solution.targets";

    /// <summary>The file Offramp writes; it replaces an older one on the next <c>doctor --fix</c>.</summary>
    public static string TargetsContent { get; } = ReadResource();

    /// <summary>The import a solution's restore needs, in the root <see cref="SolutionTargetsFileName"/>.</summary>
    public static readonly string[] SolutionLines =
    [
        "<!-- packages.config for dotnet restore of a solution: " + TargetsFileName + " lays out what the restore downloaded (added by offramp doctor). -->",
        "<Import Project=\"$(MSBuildThisFileDirectory)" + TargetsFileName + "\" Condition=\"'$(MSBuildRuntimeType)' == 'Core' And Exists('$(MSBuildThisFileDirectory)" + TargetsFileName + "')\" />",
    ];

    /// <summary>What <c>--fix</c> would create or change, by path; empty when both files are in place.</summary>
    public static IReadOnlyList<FileFix> Plan(string repositoryRoot) =>
        [.. Changes(repositoryRoot).Select(c => new FileFix
        {
            File = c.File,
            Applied = false,
            Diff = Offramp.Core.Output.UnifiedDiff.Create(c.Current is null ? null : c.File, c.File, c.Current ?? "", c.Updated),
        })];

    /// <summary>Writes what <see cref="Plan"/> shows, keeping a changed file's byte order mark; returns it with <c>Applied</c> set.</summary>
    public static IReadOnlyList<FileFix> Apply(string repositoryRoot)
    {
        var plan = Plan(repositoryRoot);
        foreach (var change in Changes(repositoryRoot))
        {
            var path = Path.Combine(repositoryRoot, change.File);
            File.WriteAllText(path, change.Updated, new UTF8Encoding(change.Bom));
        }

        return [.. plan.Select(f => f with { Applied = true })];
    }

    /// <summary>True when the targets file is current and the solution file imports it.</summary>
    public static bool IsInPlace(string repositoryRoot) => !Changes(repositoryRoot).Any();

    private sealed record Change(string File, string? Current, string Updated, bool Bom);

    private static IEnumerable<Change> Changes(string repositoryRoot)
    {
        var (targets, targetsBom) = Read(Path.Combine(repositoryRoot, TargetsFileName));

        // A checkout with CRLF line endings (core.autocrlf on Windows) has the same file; an update keeps them.
        if (targets?.ReplaceLineEndings("\n") != TargetsContent)
        {
            var crlf = targets?.Contains("\r\n", StringComparison.Ordinal) == true;
            yield return new Change(TargetsFileName, targets, crlf ? TargetsContent.ReplaceLineEndings("\r\n") : TargetsContent, targetsBom);
        }

        var (solution, solutionBom) = Read(Path.Combine(repositoryRoot, SolutionTargetsFileName));
        if (solution is null)
        {
            yield return new Change(SolutionTargetsFileName, null, "<Project>\n" + string.Concat(SolutionLines.Select(l => "  " + l + "\n")) + "</Project>\n", false);
        }
        else if (!solution.Contains(TargetsFileName, StringComparison.Ordinal))
        {
            yield return new Change(SolutionTargetsFileName, solution, CompileOnlyConditional.InsertBeforeClose(solution, SolutionLines, SolutionTargetsFileName), solutionBom);
        }
    }

    private static (string? Text, bool Bom) Read(string path)
    {
        if (!File.Exists(path))
        {
            return (null, false);
        }

        var bytes = File.ReadAllBytes(path);
        var bom = bytes is [0xEF, 0xBB, 0xBF, ..];
        return (new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
    }

    private static string ReadResource()
    {
        using var stream = typeof(PackagesConfigRestore).Assembly.GetManifestResourceStream("Offramp.Workspace.Doctor." + TargetsFileName)
            ?? throw new InvalidOperationException(TargetsFileName + " is not embedded in Offramp.Workspace.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }
}

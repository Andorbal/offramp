using System.Globalization;
using System.Text.Json;
using Offramp.Analyzers;
using Offramp.Core.Diagnostics;
using Offramp.Core.Paths;
using Offramp.Core.Processes;
using Offramp.Core.Progress;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Refactoring.Codemods;

/// <summary>What <c>codemod run --format-mode</c> is asked to do.</summary>
public sealed record CodemodFormatRequest
{
    public required string RepositoryRoot { get; init; }

    public required IReadOnlyList<ProjectInfo> Projects { get; init; }

    public required IReadOnlyList<Codemod> Codemods { get; init; }

    /// <summary>False for a dry run (<c>--verify-no-changes</c>).</summary>
    public required bool Apply { get; init; }

    public required IProcessRunner Processes { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }

    public IProgressSink Progress { get; init; } = NullProgressSink.Instance;
}

/// <summary>
/// <c>--format-mode</c>: <c>dotnet format analyzers PROJECT --diagnostics OFRM### --severity info</c>
/// for each project that references the Offramp.Analyzers package, which runs the same fixers
/// from the package inside the SDK's own workspace. A dry run adds <c>--verify-no-changes</c>;
/// <c>--apply</c> lets dotnet format write the files. The sites come from its report.
/// </summary>
public static class CodemodFormatter
{
    public const string PackageId = "Offramp.Analyzers";

    public static async Task<CodemodRunResult> RunAsync(CodemodFormatRequest request, CancellationToken cancellationToken)
    {
        var results = new List<CodemodProjectResult>();
        using (var phase = request.Progress.BeginPhase("Running dotnet format analyzers", 1, 1))
        {
            var projects = request.Projects.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
            for (var i = 0; i < projects.Count; i++)
            {
                phase.Report(i, projects.Count, projects[i].Id);
                if (await RunProjectAsync(request, projects[i], cancellationToken) is { Sites.Count: > 0 } result)
                {
                    results.Add(result);
                }
            }
        }

        return new CodemodRunResult
        {
            Mode = CodemodMode.Format,
            Codemods = [.. request.Codemods.Select(c => c.Name)],
            Projects = results,
            Summary = new CodemodSummary(results.Count, results.Sum(r => r.Files.Count), results.Sum(r => r.Sites.Count), 0, 0),
            Applied = request.Apply && results.Count > 0,
        };
    }

    /// <summary>The arguments of one project's run; the report goes to <paramref name="report"/> (a directory).</summary>
    public static List<string> Arguments(string projectPath, IEnumerable<Codemod> codemods, bool apply, string report)
    {
        var arguments = new List<string> { "format", "analyzers", projectPath, "--diagnostics" };
        arguments.AddRange(codemods.Select(c => c.Id));
        arguments.AddRange(["--severity", "info", "--report", report]);
        if (!apply)
        {
            arguments.Add("--verify-no-changes");
        }

        return arguments;
    }

    private static async Task<CodemodProjectResult?> RunProjectAsync(CodemodFormatRequest request, ProjectInfo project, CancellationToken cancellationToken)
    {
        if (!project.PackageReferences.Any(p => string.Equals(p.Id, PackageId, StringComparison.OrdinalIgnoreCase)))
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4506,
                $"{project.Id} does not reference {PackageId}, so dotnet format cannot run its codemods there; the project was skipped.",
                new DiagnosticLocation(project.Id));
            return null;
        }

        var report = Path.Combine(Path.GetTempPath(), "offramp-format-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(report);
        try
        {
            var spec = new ProcessSpec("dotnet", Arguments(RepoPaths.ToAbsolute(request.RepositoryRoot, project.Id), request.Codemods, request.Apply, report))
            {
                WorkingDirectory = request.RepositoryRoot,
                Timeout = TimeSpan.FromMinutes(30),
            };
            var run = await request.Processes.RunAsync(spec, cancellationToken);
            var file = Path.Combine(report, "format-report.json");
            var succeeded = run.ExitCode == 0 || (!request.Apply && run.ExitCode == 2);
            if (!succeeded || !File.Exists(file))
            {
                var output = (run.StandardError + "\n" + run.StandardOutput).Trim();
                request.Diagnostics.Report(DiagnosticCatalog.OFR4508,
                    string.Create(CultureInfo.InvariantCulture, $"`dotnet {string.Join(' ', spec.Arguments.Take(3))} ...` exited with {run.ExitCode}{(output.Length > 0 ? ": " + FirstLines(output, 5) : "")}"),
                    new DiagnosticLocation(project.Id));
                return null;
            }

            var sites = ReadReport(await File.ReadAllTextAsync(file, cancellationToken), request.RepositoryRoot, request.Codemods);
            return new CodemodProjectResult
            {
                Project = project.Id,
                Files = [.. sites.Select(s => s.File).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                Sites = sites,
            };
        }
        finally
        {
            try
            {
                Directory.Delete(report, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The sites in a dotnet format report: <c>[{ FilePath, FileChanges: [{ LineNumber, CharNumber, DiagnosticId }] }]</c>.</summary>
    public static List<CodemodSite> ReadReport(string json, string root, IReadOnlyList<Codemod> codemods)
    {
        var byId = codemods.ToDictionary(c => c.Id, c => c.Name, StringComparer.Ordinal);
        var sites = new List<CodemodSite>();
        using var document = JsonDocument.Parse(json);
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var path = entry.TryGetProperty("FilePath", out var filePath) ? filePath.GetString() : null;
            if (path is null || !entry.TryGetProperty("FileChanges", out var changes))
            {
                continue;
            }

            var file = RepoPaths.ToRepositoryRelative(root, Path.GetFullPath(path));
            foreach (var change in changes.EnumerateArray())
            {
                if (change.TryGetProperty("DiagnosticId", out var id) && id.GetString() is { } code && byId.TryGetValue(code, out var name))
                {
                    sites.Add(new CodemodSite
                    {
                        Codemod = name,
                        File = file,
                        Line = change.TryGetProperty("LineNumber", out var line) ? line.GetInt32() : 0,
                        Column = change.TryGetProperty("CharNumber", out var column) ? column.GetInt32() : 0,
                        Outcome = CodemodSiteOutcome.Rewritten,
                    });
                }
            }
        }

        return [.. sites.Distinct().OrderBy(s => s.File, StringComparer.Ordinal).ThenBy(s => s.Line).ThenBy(s => s.Column).ThenBy(s => s.Codemod, StringComparer.Ordinal)];
    }

    private static string FirstLines(string text, int count) =>
        string.Join(" | ", text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(count));
}

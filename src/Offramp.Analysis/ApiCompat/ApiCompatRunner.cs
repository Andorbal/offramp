using System.Text.Json.Nodes;
using Offramp.Core.Diagnostics;
using Offramp.Core.Git;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Core.Processes;
using Offramp.Workspace.Verification;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Analysis.ApiCompat;

public sealed record ApiCompatRequest
{
    public required string RepositoryRoot { get; init; }

    public required WorkspaceModel Model { get; init; }

    public required ProjectInfo Project { get; init; }

    /// <summary>The left target framework (default: the project's first .NET Framework target).</summary>
    public string? Left { get; init; }

    /// <summary>The right target framework (default: the project's newest modern target).</summary>
    public string? Right { get; init; }

    /// <summary>Compare the working tree with this git revision instead of two targets.</summary>
    public string? Baseline { get; init; }

    public required IProcessRunner Processes { get; init; }

    public required IGitService Git { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }
}

/// <summary>One side of the comparison: a label (a target framework, a revision, or the working tree) and its target.</summary>
public sealed record ApiCompatSide(string Label, string TargetFramework);

/// <summary>A public API difference ApiCompat reported.</summary>
/// <param name="Code">ApiCompat's rule (CP0001 type missing, CP0002 member missing, ...).</param>
/// <param name="Member">The type or member, as ApiCompat prints it.</param>
/// <param name="OnlyOn">The side that has it.</param>
/// <param name="Message">ApiCompat's message with the scratch paths replaced by the side labels.</param>
public sealed record ApiDifference(string Code, string Member, string? OnlyOn, string Message);

/// <summary>The <c>result</c> of <c>offramp audit api-compat</c> (<c>schemas/v1/api-compat.json</c>).</summary>
public sealed record ApiCompatResult
{
    public required string Project { get; init; }

    /// <summary><c>targets</c> (two target frameworks) or <c>baseline</c> (a revision against the working tree).</summary>
    public required string Mode { get; init; }

    public ApiCompatSide? Left { get; init; }

    public ApiCompatSide? Right { get; init; }

    /// <summary>The ApiCompat tool version used.</summary>
    public string? Tool { get; init; }

    public required IReadOnlyList<ApiDifference> Differences { get; init; }
}

/// <summary>
/// <c>audit api-compat</c>: builds the two sides (two target frameworks of the working tree, or
/// the working tree and a git revision checked out in a scratch work tree), then runs Microsoft's
/// ApiCompat (<c>Microsoft.DotNet.ApiCompat.Tool</c>, installed under <c>.offramp/tools/</c> at
/// the SDK's version) in strict mode, which reports differences in both directions. Each
/// difference is a finding (OFR3501 between targets, OFR3502 against the baseline).
/// </summary>
public static class ApiCompatRunner
{
    private const string ToolPackage = "Microsoft.DotNet.ApiCompat.Tool";

    public static async Task<ApiCompatResult?> RunAsync(ApiCompatRequest request, CancellationToken cancellationToken = default)
    {
        var project = request.Project;
        var baseline = request.Baseline is not null;
        var left = request.Left ?? (baseline ? Newest(project) : project.TargetFrameworks.Where(IsFramework).Order(StringComparer.Ordinal).FirstOrDefault());
        var right = request.Right ?? (baseline ? left : Newest(project));
        if (left is null || right is null || (!baseline && left == right))
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR3503,
                $"{project.Id} targets {string.Join(";", project.TargetFrameworks)}: compare two targets (--left, --right) of a multi-targeted project, or pass --baseline REVISION.",
                new DiagnosticLocation(project.Id));
            return null;
        }

        var outputs = Path.Combine(request.RepositoryRoot, ".offramp", "cache", "api-compat", Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var tool = await InstallToolAsync(request, cancellationToken);
            if (tool is null)
            {
                return null;
            }

            string? leftAssembly;
            string rightAssembly;
            ApiCompatSide leftSide;
            ApiCompatSide rightSide;
            if (baseline)
            {
                await using var scratch = await ScratchWorktree.CreateAtRevisionAsync(request.RepositoryRoot, request.Baseline!, request.Git, cancellationToken);
                if (scratch is null)
                {
                    request.Diagnostics.Report(DiagnosticCatalog.OFR3504, $"The baseline '{request.Baseline}' could not be checked out: the repository is not a git work tree or the revision does not exist.",
                        new DiagnosticLocation(project.Id));
                    return null;
                }

                leftAssembly = await BuildAsync(request, scratch.Path, left, Path.Combine(outputs, "baseline"), cancellationToken);
                (leftSide, rightSide) = (new ApiCompatSide(request.Baseline!, left), new ApiCompatSide("working tree", right));
            }
            else
            {
                leftAssembly = await BuildAsync(request, request.RepositoryRoot, left, Path.Combine(outputs, "left"), cancellationToken);
                (leftSide, rightSide) = (new ApiCompatSide(left, left), new ApiCompatSide(right, right));
            }

            var built = await BuildAsync(request, request.RepositoryRoot, right, Path.Combine(outputs, "right"), cancellationToken);
            if (leftAssembly is null || built is null)
            {
                return null;
            }

            rightAssembly = built;
            var run = await request.Processes.RunAsync(new ProcessSpec(tool.Value.Executable,
                ["--left-assembly", leftAssembly, "--right-assembly", rightAssembly, "--strict-mode"])
            {
                WorkingDirectory = request.RepositoryRoot,
                Timeout = TimeSpan.FromMinutes(10),
            }, cancellationToken);
            if (run.NotFound || run.TimedOut || (run.ExitCode != 0 && !run.StandardOutput.Contains("API compatibility errors", StringComparison.Ordinal) && !run.StandardError.Contains("API compatibility errors", StringComparison.Ordinal)))
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR3504, $"ApiCompat did not run: {First(run.StandardError + run.StandardOutput)}", new DiagnosticLocation(project.Id));
                return null;
            }

            var differences = Parse(run.StandardOutput + "\n" + run.StandardError, leftAssembly, rightAssembly, leftSide.Label, rightSide.Label);
            Report(request, baseline, differences);
            return new ApiCompatResult
            {
                Project = project.Id,
                Mode = baseline ? "baseline" : "targets",
                Left = leftSide,
                Right = rightSide,
                Tool = tool.Value.Version,
                Differences = differences,
            };
        }
        finally
        {
            if (Directory.Exists(outputs))
            {
                Directory.Delete(outputs, recursive: true);
            }
        }
    }

    private static bool IsFramework(string tfm) => tfm.StartsWith("net4", StringComparison.Ordinal);

    /// <summary>The newest modern (or standard) target, else the only one.</summary>
    private static string? Newest(ProjectInfo project) =>
        project.TargetFrameworks.Where(t => !IsFramework(t)).OrderBy(t => t, StringComparer.Ordinal).LastOrDefault()
        ?? project.TargetFrameworks.OrderBy(t => t, StringComparer.Ordinal).LastOrDefault();

    /// <summary>Installs the tool at the SDK's version (else the newest) under <c>.offramp/tools/apicompat</c>.</summary>
    private static async Task<(string Executable, string Version)?> InstallToolAsync(ApiCompatRequest request, CancellationToken cancellationToken)
    {
        var sdk = await request.Processes.RunAsync(new ProcessSpec("dotnet", ["--version"]) { WorkingDirectory = request.RepositoryRoot, Timeout = TimeSpan.FromMinutes(1) }, cancellationToken);
        var version = sdk.Succeeded ? sdk.StandardOutput.Trim() : null;
        foreach (var candidate in version is null ? new string?[] { null } : [version, null])
        {
            var directory = Path.Combine(request.RepositoryRoot, ".offramp", "tools", "apicompat", candidate ?? "latest");
            var executable = Path.Combine(directory, OperatingSystem.IsWindows() ? "apicompat.exe" : "apicompat");
            if (File.Exists(executable))
            {
                return (executable, candidate ?? "latest");
            }

            var arguments = new List<string> { "tool", "install", ToolPackage, "--tool-path", directory };
            if (candidate is not null)
            {
                arguments.AddRange(["--version", candidate]);
            }

            var install = await request.Processes.RunAsync(new ProcessSpec("dotnet", arguments) { WorkingDirectory = request.RepositoryRoot, Timeout = TimeSpan.FromMinutes(10) }, cancellationToken);
            if (install.Succeeded && File.Exists(executable))
            {
                return (executable, candidate ?? "latest");
            }

            if (candidate is null)
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR3504, $"{ToolPackage} could not be installed: {First(install.StandardError + install.StandardOutput)}", new DiagnosticLocation(request.Project.Id));
            }
        }

        return null;
    }

    /// <summary>Builds the project for one target into a folder of its own; returns the assembly, or null (OFR3504).</summary>
    private static async Task<string?> BuildAsync(ApiCompatRequest request, string root, string tfm, string output, CancellationToken cancellationToken)
    {
        var project = RepoPaths.ToAbsolute(root, request.Project.Id);
        var result = await request.Processes.RunAsync(new ProcessSpec("dotnet",
            ["build", project, "-c", "Release", "-f", tfm, "-nologo", "-v:q", "-p:OutDir=" + output + Path.DirectorySeparatorChar])
        {
            WorkingDirectory = root,
            Timeout = TimeSpan.FromMinutes(20),
        }, cancellationToken);
        var assembly = Path.Combine(output, (request.Project.AssemblyName ?? request.Project.Name) + ".dll");
        if (result.Succeeded && File.Exists(assembly))
        {
            return assembly;
        }

        var errors = First(result.StandardOutput + result.StandardError)
            .Replace(Path.GetFullPath(root) + Path.DirectorySeparatorChar, "", StringComparison.OrdinalIgnoreCase);
        request.Diagnostics.Report(DiagnosticCatalog.OFR3504, $"{request.Project.Id} did not build for {tfm}: {errors}", new DiagnosticLocation(request.Project.Id));
        return null;
    }

    /// <summary>Reads <c>CPnnnn: message</c> lines; paths become the side labels.</summary>
    internal static List<ApiDifference> Parse(string output, string leftAssembly, string rightAssembly, string leftLabel, string rightLabel)
    {
        var differences = new List<ApiDifference>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 8 || !line.StartsWith("CP", StringComparison.Ordinal) || line[6] != ':' || !line[2..6].All(char.IsAsciiDigit) || line.StartsWith("CP1", StringComparison.Ordinal))
            {
                continue;
            }

            var code = line[..6];
            var message = line[7..].Trim()
                .Replace(leftAssembly, leftLabel, StringComparison.OrdinalIgnoreCase)
                .Replace(rightAssembly, rightLabel, StringComparison.OrdinalIgnoreCase);
            var member = Quoted(message) ?? message;
            var onlyOn = OnlyOn(message, leftLabel, rightLabel);
            differences.Add(new ApiDifference(code, member, onlyOn, message));
        }

        return [.. differences.Distinct().OrderBy(d => d.Member, StringComparer.Ordinal).ThenBy(d => d.Code, StringComparer.Ordinal).ThenBy(d => d.Message, StringComparer.Ordinal)];
    }

    private static string? Quoted(string message)
    {
        var start = message.IndexOf('\'', StringComparison.Ordinal);
        var end = start < 0 ? -1 : message.IndexOf('\'', start + 1);
        return end > start ? message[(start + 1)..end] : null;
    }

    /// <summary>"exists on A but not on B" → A.</summary>
    private static string? OnlyOn(string message, string left, string right)
    {
        var at = message.IndexOf("exists on ", StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var rest = message[(at + "exists on ".Length)..];
        return rest.StartsWith(left, StringComparison.Ordinal) ? left : rest.StartsWith(right, StringComparison.Ordinal) ? right : null;
    }

    private static void Report(ApiCompatRequest request, bool baseline, List<ApiDifference> differences)
    {
        var descriptor = baseline ? DiagnosticCatalog.OFR3502 : DiagnosticCatalog.OFR3501;
        foreach (var difference in differences)
        {
            request.Diagnostics.Report(descriptor, $"{difference.Code}: {difference.Message}", new DiagnosticLocation(request.Project.Id),
                [KeyValuePair.Create<string, JsonNode?>("member", difference.Member), KeyValuePair.Create<string, JsonNode?>("apiCompat", difference.Code)]);
        }
    }

    private static string First(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var errors = lines.Where(l => l.Contains(" error ", StringComparison.Ordinal) || l.Contains("error:", StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
        return string.Join(" ", errors.Count > 0 ? errors : lines.Take(3));
    }
}

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Git;
using Offramp.Core.Model;
using Offramp.Core.Processes;
using Offramp.Core.Progress;
using Offramp.Refactoring.ChangeSets;
using Offramp.Workspace.Verification;

namespace Offramp.Refactoring.Dependencies.Consolidation;

public sealed record RestoreVerifyRequest
{
    public required string RepositoryRoot { get; init; }

    public required WorkspaceModel Model { get; init; }

    public required OfframpConfig Config { get; init; }

    public required ChangeSet ChangeSet { get; init; }

    /// <summary><c>restore</c>, or <c>build</c> (restore, then <c>offramp verify</c>'s build of the changed projects).</summary>
    public required string Mode { get; init; }

    public required IGitService Git { get; init; }

    public required IProcessRunner Processes { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }

    public IProgressSink Progress { get; init; } = NullProgressSink.Instance;
}

/// <summary>
/// Checks proposed project files with NuGet's own restore, never a resolver of our own
/// (CLAUDE.md): in a scratch copy of the repository, restore the solution as it is, write the
/// proposal, and restore again. Downgrades (NU1605), conflicts (NU1107), versions outside a
/// dependency's range (NU1608), missing PackageVersion items (NU1010), and restore errors the
/// current files do not produce block the change (<c>OFR1211</c>).
/// </summary>
public static partial class RestoreVerifier
{
    private static readonly HashSet<string> Blocking = new(StringComparer.Ordinal) { "NU1605", "NU1107", "NU1608", "NU1010" };

    public static async Task<ConsolidationVerification> VerifyAsync(RestoreVerifyRequest request, CancellationToken cancellationToken)
    {
        var root = request.RepositoryRoot;
        var files = request.Model.Inputs.Select(i => i.Path)
            .Concat(request.ChangeSet.Edits.Select(e => e.Path))
            .Where(p => File.Exists(Path.Combine(root, p)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        await using var scratch = await ScratchWorktree.CreateAsync(root, files, request.Git, cancellationToken);
        var target = request.Model.Solution ?? ".";

        List<RestoreWarning> before;
        using (request.Progress.BeginPhase("Restoring the current project files", 1, 2))
        {
            before = await RestoreAsync(request, scratch, target, cancellationToken);
        }

        foreach (var edit in request.ChangeSet.Edits)
        {
            await File.WriteAllBytesAsync(scratch.Resolve(edit.Path), edit.After, cancellationToken);
        }

        foreach (var create in request.ChangeSet.Creates)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(scratch.Resolve(create.Path))!);
            await File.WriteAllBytesAsync(scratch.Resolve(create.Path), create.Content, cancellationToken);
        }

        List<RestoreWarning> after;
        using (request.Progress.BeginPhase("Restoring the proposed project files", 2, 2))
        {
            after = await RestoreAsync(request, scratch, target, cancellationToken);
        }

        var added = after.Where(w => !before.Contains(w)).ToList();
        if (added.Count > 0)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR1211,
                string.Create(CultureInfo.InvariantCulture, $"Restore of the proposed project files reported {added.Count} new problem{(added.Count == 1 ? "" : "s")}; nothing was applied. {string.Join(" ", added.Select(w => $"{w.Code}: {w.Message}"))}"),
                data: [KeyValuePair.Create<string, JsonNode?>("warnings", new JsonArray([.. added.Select(w => (JsonNode?)new JsonObject
                {
                    ["code"] = w.Code,
                    ["project"] = w.Project,
                    ["message"] = w.Message,
                })]))]);
            return new ConsolidationVerification { Mode = request.Mode, Passed = false, Warnings = added };
        }

        if (request.Mode != "build")
        {
            return new ConsolidationVerification { Mode = request.Mode, Passed = true };
        }

        var projects = request.ChangeSet.Edits.Select(e => e.Path).Where(p => request.Model.Projects.Any(m => m.Id == p)).Order(StringComparer.Ordinal).ToList();
        var build = await VerifyRunner.RunAsync(new VerifyRequest
        {
            RepositoryRoot = scratch.Path,
            Model = request.Model,
            Config = request.Config.Verify with { Mode = "build" },
            Mode = VerifyMode.Build,
            Projects = projects.Count > 0 ? projects : [.. request.Model.Projects.Select(p => p.Id)],
            Everything = projects.Count == 0,
            Scope = "projects whose package versions change",
            TargetFramework = request.Config.TargetFramework,
            Processes = request.Processes,
            Diagnostics = request.Diagnostics,
            Progress = request.Progress,
        }, cancellationToken);
        return new ConsolidationVerification { Mode = request.Mode, Passed = build?.Passed ?? true };
    }

    /// <summary>The blocking NuGet warnings and the errors a restore of the scratch copy reports, deduplicated and sorted.</summary>
    private static async Task<List<RestoreWarning>> RestoreAsync(RestoreVerifyRequest request, ScratchWorktree scratch, string target, CancellationToken cancellationToken)
    {
        var result = await request.Processes.RunAsync(
            new ProcessSpec("dotnet", ["restore", target, "-nologo", "-v:minimal", "-nodeReuse:false", "-tl:off"])
            {
                WorkingDirectory = scratch.Path,
                Timeout = TimeSpan.FromSeconds(request.Config.Verify.TimeoutSeconds),
                Environment = new Dictionary<string, string?> { ["DOTNET_CLI_UI_LANGUAGE"] = "en", ["MSBUILDTERMINALLOGGER"] = "off" },
            },
            cancellationToken);
        return Parse(result.StandardOutput + "\n" + result.StandardError, scratch.Path);
    }

    /// <summary>
    /// Reads <c>path : warning NU1605: message [project]</c> lines. MSBuild prints each line of a
    /// multi-line message with the same prefix, so consecutive lines of one warning are joined.
    /// Project paths become repository-relative and the scratch location disappears from messages.
    /// </summary>
    internal static List<RestoreWarning> Parse(string output, string scratchRoot)
    {
        var root = Path.GetFullPath(scratchRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var warnings = new List<RestoreWarning>();
        (string File, string Code, string Severity, List<string> Lines)? current = null;
        void Flush()
        {
            if (current is not { } done || (!Blocking.Contains(done.Code) && done.Severity != "error"))
            {
                return;
            }

            var project = done.File.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? done.File[root.Length..].Replace('\\', '/').TrimStart('/') : null;
            var message = string.Join("\n", done.Lines.Select(l => l.Replace(root + Path.DirectorySeparatorChar, "", StringComparison.OrdinalIgnoreCase).Replace(root, "", StringComparison.OrdinalIgnoreCase).Trim()));
            var warning = new RestoreWarning(done.Code, project, message);
            if (!warnings.Contains(warning))
            {
                warnings.Add(warning);
            }
        }

        foreach (var line in output.Split('\n'))
        {
            var match = Line().Match(line.TrimEnd('\r'));
            if (!match.Success)
            {
                Flush();
                current = null;
                continue;
            }

            var (file, code, severity) = (match.Groups["file"].Value.Trim(), match.Groups["code"].Value, match.Groups["severity"].Value);
            if (current is { } open && open.File == file && open.Code == code && open.Severity == severity)
            {
                open.Lines.Add(match.Groups["message"].Value);
                continue;
            }

            Flush();
            current = (file, code, severity, [match.Groups["message"].Value]);
        }

        Flush();
        return [.. warnings.OrderBy(w => w.Project ?? "", StringComparer.Ordinal).ThenBy(w => w.Code, StringComparer.Ordinal).ThenBy(w => w.Message, StringComparer.Ordinal)];
    }

    [GeneratedRegex(@"^\s*(?<file>.*?)\s*:\s*(?<severity>warning|error)\s+(?<code>NU\d{4})\s*:\s*(?<message>.*?)(\s*\[[^\]]*\])?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Line();
}

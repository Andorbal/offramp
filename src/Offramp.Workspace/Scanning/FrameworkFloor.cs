using System.Text.Json.Nodes;
using NuGet.Frameworks;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Workspace.Ingest;

namespace Offramp.Workspace.Scanning;

/// <summary>
/// The .NET Framework version below which .NET Standard 2.0 consumption is unreliable
/// (<c>docs/spec/02-workspace-model.md#the-net-framework-floor</c>): a project older than that is OFR0106.
/// </summary>
public static class FrameworkFloor
{
    public const string Floor = "net472";

    private static readonly Version FloorVersion = new(4, 7, 2);

    /// <summary>The project's lowest .NET Framework target below the floor, or null.</summary>
    public static string? Below(ProjectInfo project) =>
        project.TargetFrameworks
            .Where(Tfm.IsNetFramework)
            .Select(t => (Tfm: t, NuGetFramework.Parse(t).Version))
            .Where(t => t.Version < FloorVersion)
            .OrderBy(t => t.Version)
            .Select(t => t.Tfm)
            .FirstOrDefault();

    /// <summary>Reports OFR0106 for a project below the floor.</summary>
    public static Diagnostic? Check(ProjectInfo project, DiagnosticBag diagnostics)
    {
        if (Below(project) is not { } below)
        {
            return null;
        }

        return diagnostics.Report(DiagnosticCatalog.OFR0106,
            $"Targets {below}, below {Floor}: raise it before porting or referencing portable projects.",
            new DiagnosticLocation(Project: project.Id),
            [KeyValuePair.Create<string, JsonNode?>("targetFramework", below), KeyValuePair.Create<string, JsonNode?>("floor", Floor)]);
    }
}

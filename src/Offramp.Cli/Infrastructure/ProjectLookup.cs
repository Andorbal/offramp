using Offramp.Core.Model;
using Offramp.Core.Paths;

namespace Offramp.Cli.Infrastructure;

/// <summary>Resolves a project named on the command line: a repository-relative path, a path relative to the working directory, or a unique project name.</summary>
public static class ProjectLookup
{
    public static string? Resolve(string value, WorkspaceModel model, CommandContext context)
    {
        var normalized = RepoPaths.Normalize(value);
        var byId = model.Projects.FirstOrDefault(p => string.Equals(p.Id, normalized, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
        {
            return byId.Id;
        }

        var absolute = Path.GetFullPath(value, context.Host.WorkingDirectory);
        var relative = RepoPaths.ToRepositoryRelative(context.Repository.Path, absolute);
        var byPath = model.Projects.FirstOrDefault(p => string.Equals(p.Id, relative, StringComparison.OrdinalIgnoreCase));
        if (byPath is not null)
        {
            return byPath.Id;
        }

        var byName = model.Projects.Where(p => string.Equals(p.Name, value, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count == 1 ? byName[0].Id : null;
    }

    /// <summary>
    /// Says that a project is hosted by another web project (<c>OFR0204</c>): it is part of the host's application
    /// (docs/decisions/0055-hosted-projects-belong-to-their-host.md). <paramref name="consequence"/> ends the sentence.
    /// </summary>
    public static void ReportHosted(ProjectInfo project, CommandContext context, string consequence)
    {
        if (project.HostedBy is not { } host)
        {
            return;
        }

        context.Diagnostics.Report(Offramp.Core.Diagnostics.DiagnosticCatalog.OFR0204,
            $"{project.Id} is hosted by {host.Project} ({string.Join("; ", host.Evidence)}): {consequence}",
            new Offramp.Core.Diagnostics.DiagnosticLocation(project.Id),
            [KeyValuePair.Create<string, System.Text.Json.Nodes.JsonNode?>("host", host.Project)]);
    }
}

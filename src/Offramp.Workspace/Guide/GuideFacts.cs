using Offramp.Core.Model;
using Offramp.Workspace.Doctor;
using Offramp.Workspace.Model;
using Offramp.Workspace.Store;

namespace Offramp.Workspace.Guide;

/// <summary>
/// The few facts about the repository that decide which guide steps apply and which are
/// done: whether offramp.yml exists, the workspace model and whether it is fresh, and
/// whether Directory.Build.props has the compile-only block. Nothing is read from source code.
/// </summary>
public sealed record GuideFacts
{
    private static readonly string[] ConfigFileNames = ["app.config", "web.config"];

    /// <summary>The target major version (10 = net10.0).</summary>
    public required int Target { get; init; }

    public required bool ConfigExists { get; init; }

    /// <summary>The workspace model; null when there is none (or it cannot be read).</summary>
    public WorkspaceModel? Model { get; init; }

    /// <summary>What changed since the model was built; null without a model.</summary>
    public Staleness? Staleness { get; init; }

    public bool CompileOnlyPresent { get; init; }

    /// <summary>Readiness per project, from the model.</summary>
    public IReadOnlyDictionary<string, ProjectStanding> Standings { get; init; } = new Dictionary<string, ProjectStanding>();

    /// <summary>Projects with an App.config or Web.config beside the project file.</summary>
    public IReadOnlySet<string> ProjectsWithConfigFile { get; init; } = new HashSet<string>();

    public bool ModelFresh => Model is not null && Staleness is { IsStale: false };

    public string TargetFramework => $"net{Target}.0";

    public bool IsReady(ProjectInfo project) =>
        Standings.TryGetValue(project.Id, out var standing) && standing.Readiness == ProjectReadiness.Ready;

    public static GuideFacts Gather(string repositoryRoot, int target, bool configExists, string workspacePath, string stateDirectory)
    {
        var model = TryReadModel(workspacePath);
        return new GuideFacts
        {
            Target = target,
            ConfigExists = configExists,
            Model = model,
            Staleness = model is null ? null : WorkspaceInputs.Compare(model, repositoryRoot, stateDirectory),
            CompileOnlyPresent = DoctorRunner.PlanFix(repositoryRoot).AlreadyPresent,
            Standings = model is null ? new Dictionary<string, ProjectStanding>() : Readiness.Compute(model),
            ProjectsWithConfigFile = model is null ? new HashSet<string>() : WithConfigFile(repositoryRoot, model),
        };
    }

    private static WorkspaceModel? TryReadModel(string workspacePath)
    {
        if (!File.Exists(workspacePath))
        {
            return null;
        }

        try
        {
            return WorkspaceStore.Read(workspacePath);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or IOException)
        {
            return null;
        }
    }

    private static HashSet<string> WithConfigFile(string repositoryRoot, WorkspaceModel model)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in model.Projects)
        {
            var directory = Path.GetDirectoryName(Path.Combine(repositoryRoot, project.Id));
            if (directory is null || !Directory.Exists(directory))
            {
                continue;
            }

            if (Directory.EnumerateFiles(directory).Any(f => ConfigFileNames.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)))
            {
                found.Add(project.Id);
            }
        }

        return found;
    }
}

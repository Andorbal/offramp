using System.Text.Json.Nodes;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Json;
using Offramp.Core.Model;
using Offramp.Core.Paths;

namespace Offramp.Workspace.Store;

/// <summary>Reads and writes <c>.offramp/workspace.json</c> and checks it is fresh.</summary>
public static class WorkspaceStore
{
    public const string FileName = "workspace.json";

    public static string StateDirectory(string repositoryRoot, OfframpConfig config) =>
        Path.GetFullPath(config.Paths.State, repositoryRoot);

    public static void Save(string path, WorkspaceModel model)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, OfframpJson.Serialize(model, OfframpCoreJsonContext.Default.WorkspaceModel), new System.Text.UTF8Encoding(false));
    }

    public static WorkspaceModel Read(string path) =>
        System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(path), OfframpCoreJsonContext.Default.WorkspaceModel)
        ?? throw new InvalidDataException($"{path} is empty.");

    /// <summary>
    /// Loads the model for a command that needs it. Reports <c>OFR0001</c> when it
    /// is missing (returns null) and <c>OFR0002</c> when it is stale (an error
    /// with <paramref name="failOnStale"/>).
    /// </summary>
    public static WorkspaceModel? LoadForCommand(
        string workspacePath, string repositoryRoot, OfframpConfig config, DiagnosticBag diagnostics, bool failOnStale)
    {
        var relative = RepoPaths.ToRepositoryRelative(repositoryRoot, workspacePath);
        if (!File.Exists(workspacePath))
        {
            diagnostics.Report(DiagnosticCatalog.OFR0001,
                $"{relative} does not exist. Run `offramp scan` first.",
                data: [KeyValuePair.Create<string, JsonNode?>("path", relative)]);
            return null;
        }

        var model = Read(workspacePath);
        var staleness = WorkspaceInputs.Compare(model, repositoryRoot, StateDirectory(repositoryRoot, config));
        if (staleness.IsStale)
        {
            diagnostics.Report(DiagnosticCatalog.OFR0002,
                $"The workspace model is stale: {staleness.Describe()}. Run `offramp scan`.",
                severity: failOnStale ? Severity.Error : null,
                data:
                [
                    KeyValuePair.Create<string, JsonNode?>("changed", new JsonArray([.. staleness.Changed.Select(c => (JsonNode?)c)])),
                    KeyValuePair.Create<string, JsonNode?>("added", new JsonArray([.. staleness.Added.Select(c => (JsonNode?)c)])),
                    KeyValuePair.Create<string, JsonNode?>("removed", new JsonArray([.. staleness.Removed.Select(c => (JsonNode?)c)])),
                    KeyValuePair.Create<string, JsonNode?>("sourceChanged", staleness.SourceChanged),
                ]);
        }

        return model;
    }
}

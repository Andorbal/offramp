using Offramp.Core.Model;
using Offramp.Workspace.Store;

namespace Offramp.Fixtures;

/// <summary>
/// The scanned model of a fixture, from its verified snapshot in
/// Offramp.Workspace.Tests (scrubbed of paths, hashes, and timestamps), so
/// consumers of the model test against exactly what scan produces without
/// building the fixture again.
/// </summary>
public static class FixtureModels
{
    public static WorkspaceModel Load(string fixture) =>
        WorkspaceStore.Read(RepositoryFiles.Path(
            "tests", "Offramp.Workspace.Tests", "Snapshots",
            $"ScanFixtureTests.Model_matches_the_snapshot_and_the_schema_fixture={fixture}.verified.json"));
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Offramp.Fixtures;

namespace Offramp.Corpus.Tests.Harness;

/// <summary>
/// One entry of <c>codebases.json</c>: a real codebase pinned to one commit. The workflow
/// (<c>.github/workflows/corpus.yml</c>) builds its job matrix from the same file.
/// </summary>
public sealed record CorpusCodebase
{
    /// <summary>Short id: the <c>Codebase</c> trait of its test class, the <c>OFFRAMP_CORPUS</c> selector, and the job name.</summary>
    public required string Name { get; init; }

    /// <summary>What it is, for messages: "DotNetNuke Platform 9.13.10".</summary>
    public required string Title { get; init; }

    /// <summary>The git URL to fetch from.</summary>
    public required string Repository { get; init; }

    /// <summary>The full 40-character commit the test runs on. A tag can move; a commit cannot.</summary>
    public required string Commit { get; init; }

    /// <summary>The tag or branch the commit came from, for people; never used to fetch.</summary>
    public string? Ref { get; init; }

    /// <summary>The solution <c>offramp.yml</c> names, relative to the repository root.</summary>
    public required string Solution { get; init; }

    /// <summary>The CI job's limit, which includes building Offramp; the test's own timeout must be lower.</summary>
    public required int TimeoutMinutes { get; init; }

    /// <summary>The field-test report the test's assertions come from.</summary>
    public required string FieldTest { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly Lazy<IReadOnlyList<CorpusCodebase>> Loaded = new(() =>
        JsonSerializer.Deserialize<List<CorpusCodebase>>(File.ReadAllText(ManifestPath), Options)
            ?? throw new InvalidDataException($"{ManifestPath} is empty."));

    public static string ManifestPath => RepositoryFiles.Path("tests", "Offramp.Corpus.Tests", "codebases.json");

    public static IReadOnlyList<CorpusCodebase> All => Loaded.Value;

    public static CorpusCodebase Get(string name) =>
        All.SingleOrDefault(c => c.Name == name)
            ?? throw new InvalidOperationException($"'{name}' is not in {ManifestPath}.");
}

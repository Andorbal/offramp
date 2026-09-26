using System.Collections.Concurrent;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Processes;
using Offramp.Workspace.Scanning;
using Offramp.Workspace.Store;

namespace Offramp.Fixtures;

/// <summary>A fixture copied, built, and scanned once per test run.</summary>
public sealed record ScannedFixture(FixtureRepository Repository, ScanOutcome Outcome, DiagnosticBag Diagnostics)
{
    public string Root => Repository.Path;

    public string WorkspacePath => Path.Combine(Root, ".offramp", WorkspaceStore.FileName);

    public string ModelJson => File.ReadAllText(WorkspacePath);
}

public static class ScannedFixtures
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<ScannedFixture>>> Cache = new(StringComparer.Ordinal);

    static ScannedFixtures()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var entry in Cache.Values.Where(v => v.IsValueCreated && v.Value.IsCompletedSuccessfully))
            {
                entry.Value.Result.Repository.Dispose();
            }
        };
    }

    /// <summary>Scans a fixture; the windows-only fixture is scanned from its committed binlog.</summary>
    public static Task<ScannedFixture> GetAsync(string name) =>
        Cache.GetOrAdd(name, n => new Lazy<Task<ScannedFixture>>(() => ScanAsync(n))).Value;

    public static async Task<ScannedFixture> ScanAsync(string name, Func<string, ScanRequest, ScanRequest>? customize = null) =>
        await ScanAsync(await FixtureRepository.CreateAsync(name), customize);

    /// <summary>Generates a fixture (see <see cref="GeneratedFixtures"/>), then builds and scans it.</summary>
    public static async Task<ScannedFixture> ScanGeneratedAsync(string name, Action<string> generate) =>
        await ScanAsync(await FixtureRepository.CreateAsync(name, generate), null);

    private static async Task<ScannedFixture> ScanAsync(FixtureRepository repository, Func<string, ScanRequest, ScanRequest>? customize)
    {
        var name = repository.Name;
        var diagnostics = new DiagnosticBag();
        var request = Request(repository.Path, diagnostics);
        if (name == "windows-only-build-steps")
        {
            request = request with { BinlogPath = Path.Combine(repository.Path, "msbuild.binlog") };
        }

        // Legacy packages.config projects build against packages/, which nuget restore fills on Windows.
        await PackagesConfigRestore.RestoreAsync(repository.Path);

        request = customize?.Invoke(repository.Path, request) ?? request;
        var outcome = await ScanRunner.RunAsync(request, CancellationToken.None);
        return new ScannedFixture(repository, outcome, diagnostics);
    }

    public static ScanRequest Request(string root, DiagnosticBag diagnostics, IProcessRunner? processes = null) => new()
    {
        RepositoryRoot = root,
        Config = new OfframpConfig(),
        WorkspacePath = Path.Combine(root, ".offramp", WorkspaceStore.FileName),
        Processes = processes ?? ProcessRunner.Instance,
        Diagnostics = diagnostics,
        Time = new FakeTimeProvider(),
    };
}

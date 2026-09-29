using System.Text.Json.Nodes;
using Offramp.Core.Processes;
using Offramp.Fixtures;

namespace Offramp.Corpus.Tests;

/// <summary>
/// DotNetNuke Platform 9.13.10, the codebase of <c>docs/field-tests/2026-09-dnn-platform-9.13.10.md</c>: a Web
/// Forms CMS with 71 projects, 64 of them legacy and on packages.config. Opt-in (<c>Category=Corpus</c> and
/// <c>OFFRAMP_CORPUS=1</c>, set by <c>corpus.yml</c>): it clones the tag once (cached under <c>tests/.cache/corpus/</c>), runs the tool on a fresh
/// copy as a user would, and checks that what the field test found wrong stays fixed.
/// </summary>
[Trait("Category", "Corpus")]
public sealed class DotNetNukeTests
{
    private const string Repository = "https://github.com/dnnsoftware/Dnn.Platform.git";
    private const string Tag = "v9.13.10";
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromHours(1);

    [Fact]
    public async Task A_fresh_checkout_names_every_blocker_and_the_field_test_findings_stay_fixed()
    {
        // Slow and online: a plain `dotnet test` leaves it out; corpus.yml opts in.
        Assert.SkipUnless(System.Environment.GetEnvironmentVariable("OFFRAMP_CORPUS") == "1", "Set OFFRAMP_CORPUS=1 to run the corpus tests.");
        using var repo = await CheckoutAsync();

        // DotNetNuke pins SDK 9.0.202 with latestMinor; let the SDK that runs the tests build it.
        repo.Write("global.json", "{ \"sdk\": { \"version\": \"9.0.100\", \"rollForward\": \"latestMajor\" } }\n");
        repo.Write("offramp.yml", "version: 1\nsolution: DNN_Platform.sln\nverify:\n  timeoutSeconds: 3600\n");

        var doctor = await OfframpAsync(repo, "doctor", "--fix", "--apply", "--yes");
        Assert.True(doctor.ExitCode == 0, doctor.Text);
        Assert.DoesNotContain(Codes(doctor), c => c == "OFR1303");

        // Offramp supplies the reference assemblies, web targets, and packages.config packages; the build
        // still stops at DotNetNuke's own problems outside Windows (XCOPY targets, and on Linux letter case),
        // and scan names each one. Projects MSBuild then skips name the reference that failed.
        var scan = await OfframpAsync(repo, "scan");
        Assert.True(scan.ExitCode is 0 or 1, scan.Text);
        var result = scan.Json["result"]!;
        var notLoaded = result["notLoaded"]!.AsArray();
        Assert.Equal(71, result["projects"]!.GetValue<int>() + notLoaded.Count);
        Assert.All(notLoaded, n => Assert.DoesNotContain("no evaluation", n!["reason"]!.GetValue<string>(), StringComparison.Ordinal));
        Assert.Contains("OFR0106", Codes(scan));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Contains(Diagnostics(scan, "OFR0115"), d => d["project"]!.GetValue<string>().EndsWith("DotNetNuke.Abstractions.csproj", StringComparison.Ordinal));
        }

        if (OperatingSystem.IsLinux())
        {
            Assert.Contains("OFR0117", Codes(scan));
        }

        Assert.Contains(Diagnostics(scan, "OFR0121"), d => d["project"]!.GetValue<string>().EndsWith("DotNetNuke.DependencyInjection.csproj", StringComparison.Ordinal));

        // plan: a netstandard2.0 project referencing a net472 legacy project is blocked, not done.
        var plan = await OfframpAsync(repo, "plan");
        var injection = plan.Json["result"]!["order"]!.AsArray().Single(e => e!["name"]!.GetValue<string>() == "DotNetNuke.DependencyInjection")!;
        Assert.Equal("blocked", injection["readiness"]!.GetValue<string>());

        // deps resolve-dlls: packages.config DLLs are that package, not a guess from the assembly version.
        var dlls = await OfframpAsync(repo, "deps", "resolve-dlls");
        var summary = dlls.Json["result"]!["summary"]!;
        Assert.True(summary["packagesConfig"]!.GetValue<int>() > 250, summary.ToJsonString());
        Assert.True(summary["blockers"]!.GetValue<int>() < 20, summary.ToJsonString());

        // redirects sync --prune: no live redirect of a packages.config package is removed, and applications
        // whose model is partial are left alone rather than guessed at.
        var redirects = await OfframpAsync(repo, "redirects", "sync", "--prune");
        var pruned = redirects.Json["result"]!["apps"]!.AsArray()
            .SelectMany(a => a!["redirects"]!.AsArray())
            .Where(r => r!["action"]!.GetValue<string>() == "pruned")
            .Select(r => r!["assembly"]!.GetValue<string>());
        Assert.DoesNotContain("Newtonsoft.Json", pruned);
        Assert.DoesNotContain("BouncyCastle.Crypto", pruned);

        // deps audit sees the packages.config packages (23 before, 77 after the fix).
        var audit = await OfframpAsync(repo, "deps", "audit");
        Assert.True(audit.Json["result"]!["packages"]!.AsArray().Count >= 70, audit.Json["result"]!["summary"]!.ToJsonString());
    }

    private sealed record Run(int ExitCode, JsonNode Json, string Text);

    private static async Task<Run> OfframpAsync(ScratchDirectory repo, params string[] arguments)
    {
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Release" : "Debug";
        var tool = RepositoryFiles.Path("src", "Offramp.Cli", "bin", configuration, "net10.0", "offramp.dll");
        var run = await ProcessRunner.Instance.RunAsync(
            new ProcessSpec("dotnet", [tool, .. arguments, "--json", "--no-llm"]) { WorkingDirectory = repo.Path, Timeout = BuildTimeout },
            TestContext.Current.CancellationToken);
        var text = $"offramp {string.Join(' ', arguments)}: exit {run.ExitCode}\n{run.StandardOutput}\n{run.StandardError}";
        return new Run(run.ExitCode, JsonNode.Parse(run.StandardOutput) ?? throw new InvalidOperationException(text), text);
    }

    private static IEnumerable<string> Codes(Run run) =>
        run.Json["diagnostics"]!.AsArray().Select(d => d!["code"]!.GetValue<string>());

    private static IEnumerable<JsonNode> Diagnostics(Run run, string code) =>
        run.Json["diagnostics"]!.AsArray().Where(d => d!["code"]!.GetValue<string>() == code).Select(d => d!);

    /// <summary>A fresh copy of the tag: cloned once into the cache, then cloned locally for each run.</summary>
    private static async Task<ScratchDirectory> CheckoutAsync()
    {
        var cache = RepositoryFiles.Path("tests", ".cache", "corpus", "Dnn.Platform-" + Tag);
        if (!Directory.Exists(Path.Combine(cache, ".git")))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            await GitAsync(Path.GetDirectoryName(cache)!, "clone", "--quiet", "--depth", "1", "--branch", Tag, Repository, cache);
        }

        var copy = new ScratchDirectory("dnn");
        Directory.Delete(copy.Path);
        await GitAsync(Path.GetDirectoryName(copy.Path)!, "clone", "--quiet", "--local", cache, copy.Path);
        return copy;
    }

    private static async Task GitAsync(string directory, params string[] arguments)
    {
        var git = await ProcessRunner.Instance.RunAsync(
            new ProcessSpec("git", arguments) { WorkingDirectory = directory, Timeout = TimeSpan.FromMinutes(20) },
            TestContext.Current.CancellationToken);
        Assert.True(git.Succeeded, $"git {string.Join(' ', arguments)}: {git.StandardError}");
    }
}

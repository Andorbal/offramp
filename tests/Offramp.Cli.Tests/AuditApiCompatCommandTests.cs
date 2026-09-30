using System.Text.Json.Nodes;
using Offramp.Core.Processes;
using Offramp.Fixtures;
using Offramp.Workspace.Doctor;

namespace Offramp.Cli.Tests;

/// <summary><c>audit api-compat</c> on <c>dual-target</c>, with the real SDK and ApiCompat tool.</summary>
public sealed class AuditApiCompatCommandTests
{
    [Fact]
    [ProducesDiagnostic("OFR3501")]
    public async Task A_member_only_one_target_has_is_reported_and_identical_targets_are_clean()
    {
        var fixture = await ScannedFixtures.ScanAsync("dual-target");
        using var repository = fixture.Repository;
        repository.Directory.Write("offramp.yml", "version: 1\n");
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds().WithRealTools();

        var clean = await cli.RunAsync("audit", "api-compat", "--project", "src/Shared/Shared.csproj", "--json");
        var clock = repository.Directory.Read("src/Shared/Clock.cs");
        repository.Directory.Write("src/Shared/Clock.cs", clock.Replace("#else", "    public static string Zone() => \"local\";\n#else", StringComparison.Ordinal));
        var divergent = await cli.RunAsync("audit", "api-compat", "--project", "Shared", "--json");

        Assert.Equal(0, clean.ExitCode);
        SchemaAssert.ValidEnvelope(clean.Out, "api-compat");
        Assert.Empty(JsonNode.Parse(clean.Out)!["result"]!["differences"]!.AsArray());
        Assert.Equal(0, divergent.ExitCode);
        var result = JsonNode.Parse(divergent.Out)!;
        var difference = Assert.Single(result["result"]!["differences"]!.AsArray())!;
        Assert.Equal(("CP0002", "string Shared.Clock.Zone()", "net48"), (difference["code"]!.GetValue<string>(), difference["member"]!.GetValue<string>(), difference["onlyOn"]!.GetValue<string>()));
        Assert.Equal("Member 'string Shared.Clock.Zone()' exists on net48 but not on net10.0", difference["message"]!.GetValue<string>());
        Assert.Contains(result["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR3501");
        result["result"]!["tool"] = "{Tool}";
        await Verify(result["result"]!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), extension: "json");
    }

    [Fact]
    [ProducesDiagnostic("OFR3502")]
    public async Task The_working_tree_is_compared_with_a_baseline_revision()
    {
        var fixture = await ScannedFixtures.ScanAsync("dual-target");
        using var repository = fixture.Repository;
        repository.Directory.Write("offramp.yml", "version: 1\n");
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds().WithRealTools();
        var formatter = repository.Directory.Read("src/Shared/Formatter.cs");
        repository.Directory.Write("src/Shared/Formatter.cs", formatter.Replace(
            "    public string Format(Order order) => JsonConvert.SerializeObject(order);\n",
            "    public string Format(Order order) => JsonConvert.SerializeObject(order);\n\n    public string Name() => \"shared\";\n",
            StringComparison.Ordinal));

        var run = await cli.RunAsync("audit", "api-compat", "--project", "Shared", "--baseline", "HEAD", "--json");

        Assert.True(run.ExitCode == 0, run.ToString());
        SchemaAssert.ValidEnvelope(run.Out, "api-compat");
        var result = JsonNode.Parse(run.Out)!["result"]!;
        Assert.Equal(("baseline", "HEAD", "working tree"), (result["mode"]!.GetValue<string>(), result["left"]!["label"]!.GetValue<string>(), result["right"]!["label"]!.GetValue<string>()));
        var added = Assert.Single(result["differences"]!.AsArray())!;
        Assert.Equal(("string Shared.Formatter.Name()", "working tree"), (added["member"]!.GetValue<string>(), added["onlyOn"]!.GetValue<string>()));
    }

    /// <summary>
    /// NHibernate P1 #10: the working tree failed with MSB3073 because the builds were <c>-c Release</c>, which runs
    /// NHibernate's Release-only ILRepack step, and ignored <c>verify.properties</c> (<c>RestorePackages=false</c>).
    /// Both sides build as verification does (ADR 0058).
    /// </summary>
    [Fact]
    public async Task Both_sides_build_with_the_verify_configuration_and_properties()
    {
        var fixture = await ScannedFixtures.GetAsync("dual-target");
        using var settings = new ScratchDirectory("api-compat-config");
        var config = settings.Write("offramp.yml", "version: 1\nverify:\n  configuration: Checked\n  properties:\n    SignAssembly: \"false\"\n");
        using var cli = new CliHarness(fixture.Repository.Directory);
        var builds = new List<IReadOnlyList<string>>();
        cli.Machine.Setup.Add(r => r
            .On("dotnet", ["--version"], 0, "10.0.100\n")
            .On("dotnet", ["tool", "install"], spec =>
            {
                var directory = spec.Arguments[spec.Arguments.ToList().IndexOf("--tool-path") + 1];
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, OperatingSystem.IsWindows() ? "apicompat.exe" : "apicompat"), "");
                return new ProcessResult(0, "installed", "");
            })
            .On("dotnet", ["build"], spec =>
            {
                lock (builds)
                {
                    builds.Add(spec.Arguments);
                }

                var output = spec.Arguments.Single(a => a.StartsWith("-p:OutDir=", StringComparison.Ordinal))["-p:OutDir=".Length..];
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "Shared.dll"), "");
                return new ProcessResult(0, "", "");
            })
            .On(spec => Path.GetFileNameWithoutExtension(spec.FileName) == "apicompat", _ => new ProcessResult(0, "", "")));

        var run = await cli.RunAsync("audit", "api-compat", "--project", "Shared", "--config", config, "--json");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(2, builds.Count);
        Assert.All(builds, arguments =>
        {
            Assert.Equal("Checked", arguments[arguments.ToList().IndexOf("-c") + 1]);
            Assert.Contains("-p:SignAssembly=false", arguments);
            Assert.Equal(!OperatingSystem.IsWindows(), arguments.Contains("-p:RestorePackages=false"));
            Assert.DoesNotContain("Release", arguments);
        });
    }

    /// <summary>
    /// NHibernate P1 #10: the baseline's scratch work tree had neither the compile-only block that <c>doctor --fix</c>
    /// wrote (uncommitted) nor the git-ignored <c>src/SharedAssemblyInfo.cs</c> that NAnt generates, so it failed
    /// (MSB3644). It gets Offramp's compile-only sections and the git-ignored files the working tree compiles (ADR 0058).
    /// </summary>
    [Fact]
    [ProducesDiagnostic("OFR3505")]
    public async Task The_baseline_gets_the_compile_only_sections_and_the_git_ignored_files_the_working_tree_compiles()
    {
        var fixture = await ScannedFixtures.ScanAsync("dual-target", (root, request) =>
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), "bin/\nobj/\n.offramp/\nsrc/Shared/BuildInfo.g.cs\n");
            File.WriteAllText(Path.Combine(root, "src", "Shared", "BuildInfo.g.cs"), "namespace Shared;\n\npublic static class BuildInfo\n{\n    public const string Version = \"4.1.2\";\n}\n");
            var formatter = Path.Combine(root, "src", "Shared", "Formatter.cs");
            File.WriteAllText(formatter, File.ReadAllText(formatter).Replace(
                "    public string Format(Order order) => JsonConvert.SerializeObject(order);\n",
                "    public string Format(Order order) => JsonConvert.SerializeObject(order);\n\n    public string Version() => BuildInfo.Version;\n",
                StringComparison.Ordinal));
            return request;
        });
        using var repository = fixture.Repository;
        await repository.GitAsync("add", ".gitignore", "src/Shared/Formatter.cs");
        await repository.GitAsync("commit", "-q", "-m", "version from a generated file");
        DoctorRunner.ApplyFix(repository.Path);
        repository.Directory.Write("offramp.yml", "version: 1\n");
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds().WithRealTools();
        var compileOnly = new List<(string Root, bool Present)>();
        cli.Machine.Setup.Add(r => r.On("dotnet", ["build"], spec =>
        {
            var props = Path.Combine(spec.WorkingDirectory!, "Directory.Build.props");
            lock (compileOnly)
            {
                compileOnly.Add((spec.WorkingDirectory!, File.ReadAllText(props).Contains(CompileOnlyConditional.Marker, StringComparison.Ordinal)));
            }

            return ProcessRunner.Instance.RunAsync(spec).GetAwaiter().GetResult();
        }));

        var run = await cli.RunAsync("audit", "api-compat", "--project", "Shared", "--baseline", "HEAD", "--json");

        Assert.True(run.ExitCode == 0, run.ToString());
        var envelope = JsonNode.Parse(run.Out)!;
        Assert.Empty(envelope["result"]!["differences"]!.AsArray());
        var copied = Assert.Single(envelope["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR3505")!;
        Assert.Equal("The baseline HEAD was built with 1 git-ignored file copied from the working tree, which its compilation uses and no revision has: src/Shared/BuildInfo.g.cs.", copied["message"]!.GetValue<string>());
        Assert.Equal(2, compileOnly.Count);
        Assert.All(compileOnly, c => Assert.True(c.Present, $"no compile-only block in {c.Root}"));
        Assert.Contains(compileOnly, c => c.Root != repository.Path);
    }

    [Fact]
    [ProducesDiagnostic("OFR3503")]
    public async Task A_single_target_project_without_a_baseline_has_nothing_to_compare()
    {
        var fixture = await ScannedFixtures.GetAsync("dual-target");
        using var cli = new CliHarness(fixture.Repository.Directory);

        var run = await cli.RunAsync("audit", "api-compat", "--project", "src/Tool/Tool.csproj", "--json");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("OFR3503", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    [ProducesDiagnostic("OFR3504")]
    public async Task A_side_that_does_not_build_is_an_environment_failure()
    {
        var fixture = await ScannedFixtures.GetAsync("dual-target");
        using var cli = new CliHarness(fixture.Repository.Directory);
        cli.Machine.Setup.Add(r => r
            .On("dotnet", ["--version"], 0, "10.0.100\n")
            .On("dotnet", ["tool", "install"], spec =>
            {
                var directory = spec.Arguments[spec.Arguments.ToList().IndexOf("--tool-path") + 1];
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, OperatingSystem.IsWindows() ? "apicompat.exe" : "apicompat"), "");
                return new ProcessResult(0, "installed", "");
            })
            .On("dotnet", ["build"], 1, "Shared.cs(3,1): error CS1002: ; expected", ""));

        var run = await cli.RunAsync("audit", "api-compat", "--project", "Shared", "--json");

        Assert.Equal(3, run.ExitCode);
        Assert.Contains("did not build for net48: Shared.cs(3,1): error CS1002: ; expected", run.Out, StringComparison.Ordinal);
    }
}

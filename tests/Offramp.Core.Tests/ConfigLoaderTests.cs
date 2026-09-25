using System.Text.Json.Nodes;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Json;
using Offramp.Fixtures;

namespace Offramp.Core.Tests;

public sealed class ConfigLoaderTests : IDisposable
{
    private readonly ScratchDirectory _repo = new("config");

    public void Dispose() => _repo.Dispose();

    private ConfigLoadResult Load(
        IReadOnlyDictionary<string, string>? environment = null, JsonObject? commandLine = null, string? explicitPath = null) =>
        ConfigLoader.Load(new ConfigSources
        {
            RepositoryRoot = _repo.Path,
            ExplicitPath = explicitPath,
            Environment = environment ?? new Dictionary<string, string>(),
            CommandLine = commandLine,
        });

    [Fact]
    [ProducesDiagnostic("OFR0016")]
    public void Without_a_file_the_built_in_defaults_apply()
    {
        var result = Load();

        Assert.True(result.IsValid);
        Assert.Null(result.File);
        Assert.Equal(10, result.Config.Target);
        Assert.Equal("net10.0", result.Config.TargetFramework);
        Assert.Equal("build", result.Config.Verify.Mode);
        Assert.Equal(1800, result.Config.Verify.TimeoutSeconds);
        Assert.Equal(".offramp", result.Config.Paths.State);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("OFR0016", diagnostic.Code);
        Assert.Equal(Severity.Info, diagnostic.Severity);
    }

    [Fact]
    public void Precedence_is_defaults_then_file_then_environment_then_command_line()
    {
        _repo.Write("offramp.yml", "target: 8\nverify:\n  timeoutSeconds: 600\n  mode: none\n");

        var fileOnly = Load();
        Assert.Equal(8, fileOnly.Config.Target);
        Assert.Equal(600, fileOnly.Config.Verify.TimeoutSeconds);
        Assert.Equal("Debug", fileOnly.Config.Verify.Configuration);

        var environment = new Dictionary<string, string>
        {
            ["OFFRAMP_TARGET"] = "9",
            ["OFFRAMP_VERIFY__TIMEOUT_SECONDS"] = "1200",
        };
        var withEnvironment = Load(environment);
        Assert.Equal(9, withEnvironment.Config.Target);
        Assert.Equal(1200, withEnvironment.Config.Verify.TimeoutSeconds);
        Assert.Equal("none", withEnvironment.Config.Verify.Mode);

        var withFlags = Load(environment, new JsonObject { ["target"] = 11 });
        Assert.Equal(11, withFlags.Config.Target);
        Assert.Equal(1200, withFlags.Config.Verify.TimeoutSeconds);
        Assert.Equal(11, withFlags.Effective["target"]!.GetValue<int>());
    }

    [Fact]
    public void Environment_aliases_and_nested_keys_map_to_settings()
    {
        var result = Load(new Dictionary<string, string>
        {
            ["OFFRAMP_STATE"] = ".state",
            ["OFFRAMP_LLM_URL"] = "http://llm.local/v1",
            ["OFFRAMP_LLM_MODEL"] = "qwen",
            ["OFFRAMP_LLM_PROVIDER"] = "anthropic",
            ["OFFRAMP_VERIFY__NO_WARN"] = "CS1591;NU1603",
            ["OFFRAMP_VERIFY__RESTORE"] = "false",
            ["OFFRAMP_SERVICE__K8S"] = "true",
            ["OFFRAMP_LLM_API_KEY"] = "secret-never-in-config",
            ["OFFRAMP_SOMETHING_UNRELATED"] = "ignored",
            ["PATH"] = "/usr/bin",
        });

        Assert.True(result.IsValid);
        Assert.Equal(".state", result.Config.Paths.State);
        Assert.Equal("http://llm.local/v1", result.Config.Llm.Url);
        Assert.Equal("qwen", result.Config.Llm.Model);
        Assert.Equal("anthropic", result.Config.Llm.Provider);
        Assert.Equal(["CS1591", "NU1603"], result.Config.Verify.NoWarn);
        Assert.False(result.Config.Verify.Restore);
        Assert.True(result.Config.Service.K8s);
        Assert.DoesNotContain("secret-never-in-config", result.Effective.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("OFFRAMP_TARGET", "ten")]
    [InlineData("OFFRAMP_VERIFY__MODE", "compile")]
    [InlineData("OFFRAMP_VERIFY__RESTORE", "perhaps")]
    [ProducesDiagnostic("OFR0056")]
    public void Invalid_environment_values_are_reported(string name, string value)
    {
        var result = Load(new Dictionary<string, string> { [name] = value });

        Assert.False(result.IsValid);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == "OFR0056");
        Assert.Contains(name, diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    [ProducesDiagnostic("OFR0050")]
    public void Unknown_keys_warn_with_location_and_suggestion_and_are_dropped()
    {
        _repo.Write("offramp.yml", "target: 9\nverfy:\n  mode: none\nverify:\n  timeout: 5\n");

        var result = Load();

        Assert.True(result.IsValid);
        var unknown = result.Diagnostics.Where(d => d.Code == "OFR0050").ToList();
        Assert.Equal(2, unknown.Count);
        var typo = unknown.Single(d => d.Message.Contains("'verfy'", StringComparison.Ordinal));
        Assert.Equal(Severity.Warning, typo.Severity);
        Assert.Equal("offramp.yml", typo.File);
        Assert.Equal(2, typo.Line);
        Assert.Equal(1, typo.Column);
        Assert.Contains("Did you mean 'verify'?", typo.Message, StringComparison.Ordinal);
        Assert.Contains(unknown, d => d.Message.Contains("'verify.timeout'", StringComparison.Ordinal) && d.Line == 5);
        Assert.Null(result.Effective["verfy"]);
        Assert.Equal("build", result.Config.Verify.Mode);
        Assert.Equal(9, result.Config.Target);
    }

    [Fact]
    [ProducesDiagnostic("OFR0053")]
    public void Invalid_values_make_the_configuration_invalid()
    {
        _repo.Write("offramp.yml", "target: ten\nverify:\n  mode: compile\n");

        var result = Load();

        Assert.False(result.IsValid);
        var errors = result.Diagnostics.Where(d => d.Code == "OFR0053").OrderBy(d => d.Line).ToList();
        Assert.Equal(2, errors.Count);
        Assert.Equal(1, errors[0].Line);
        Assert.Contains("verify.mode", errors[1].Message, StringComparison.Ordinal);
        Assert.Contains("build, command, none", errors[1].Message, StringComparison.Ordinal);
        Assert.Equal(3, errors[1].Line);
        Assert.Equal(10, result.Config.Target);
    }

    [Fact]
    [ProducesDiagnostic("OFR0054")]
    public void Yaml_syntax_errors_are_reported_with_position()
    {
        _repo.Write("offramp.yml", "target: 9\nverify:\n  mode: \"build\n");

        var result = Load();

        Assert.False(result.IsValid);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("OFR0054", diagnostic.Code);
        Assert.NotNull(diagnostic.Line);
    }

    [Fact]
    [ProducesDiagnostic("OFR0055")]
    public void A_missing_explicit_file_is_an_error()
    {
        var result = Load(explicitPath: "does-not-exist.yml");

        Assert.False(result.IsValid);
        Assert.Equal("OFR0055", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Offramp_config_environment_variable_selects_the_file()
    {
        _repo.Write("config/custom.yml", "target: 7\n");

        var result = Load(new Dictionary<string, string> { ["OFFRAMP_CONFIG"] = "config/custom.yml" });

        Assert.Equal(7, result.Config.Target);
        Assert.Equal("config/custom.yml", result.File);
    }

    [Fact]
    [ProducesDiagnostic("OFR0051")]
    [ProducesDiagnostic("OFR0052")]
    public void Pins_and_overrides_without_reasons_are_reported()
    {
        _repo.Write("offramp.yml", """
            deps:
              pins:
                - package: log4net
                  version: 2.0.15
                - package: Newtonsoft.Json
                  version: 9.0.1
                  reason: "Customer integrations"
            rules:
              OFR3105: { severity: none }
              OFR3210: { severity: error, reason: "Policy" }
            """);

        var result = Load();

        Assert.True(result.IsValid);
        var pin = Assert.Single(result.Diagnostics, d => d.Code == "OFR0051");
        Assert.Equal(Severity.Warning, pin.Severity);
        Assert.Contains("log4net", pin.Message, StringComparison.Ordinal);
        Assert.Equal(3, pin.Line);
        var rule = Assert.Single(result.Diagnostics, d => d.Code == "OFR0052");
        Assert.Equal(Severity.Info, rule.Severity);
        Assert.Contains("OFR3105", rule.Message, StringComparison.Ordinal);

        var overrides = result.Config.SeverityOverrides();
        Assert.Null(overrides["OFR3105"].Severity);
        Assert.Equal(Severity.Error, overrides["OFR3210"].Severity);
        Assert.Equal("Policy", overrides["OFR3210"].Reason);
    }

    [Fact]
    public void Numeric_looking_versions_and_properties_bind_as_their_literal_text()
    {
        _repo.Write("offramp.yml", """
            deps:
              pins:
                - package: Foo
                  version: 2.0
                  reason: r
            verify:
              properties:
                LangVersion: 7.3
                TreatWarningsAsErrors: false
            """);

        var result = Load();

        Assert.True(result.IsValid, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal("2.0", result.Config.Deps.Pins[0].Version);
        Assert.Equal("7.3", result.Config.Verify.Properties["LangVersion"]);
        Assert.Equal("false", result.Config.Verify.Properties["TreatWarningsAsErrors"]);
    }

    [Fact]
    public void The_full_example_from_the_specification_loads()
    {
        var spec = File.ReadAllText(RepositoryFiles.Path("docs", "spec", "03-configuration.md"));
        var start = spec.IndexOf("```yaml", StringComparison.Ordinal) + "```yaml".Length;
        var end = spec.IndexOf("```", start, StringComparison.Ordinal);
        _repo.Write("offramp.yml", spec[start..end]);

        var result = Load();

        Assert.True(result.IsValid, string.Join("\n", result.Diagnostics.Select(d => $"{d.Code} {d.Message}")));
        Assert.Equal(["OFR0052"], result.Diagnostics.Select(d => d.Code).Distinct());
        Assert.Equal("src/Monolith.sln", result.Config.Solution);
        Assert.Equal("9.0.1", result.Config.Deps.Pins[0].Version);
        Assert.Equal("eng/Packages.props", result.Config.Deps.Cpm.File);
        Assert.Equal("Off", result.Config.Verify.Properties["GenerateSerializationAssemblies"]);
        Assert.Equal(["desktop"], result.Config.Rules.Packs.Disable);
    }

    [Fact]
    public void The_effective_configuration_is_sorted_deterministic_and_matches_the_schema()
    {
        _repo.Write("offramp.yml", "rules:\n  OFR3210: { severity: error, reason: r }\n  OFR0001: { severity: warning, reason: r }\n");

        var first = OfframpJson.Format(Load().Effective);
        var second = OfframpJson.Format(Load().Effective);

        Assert.Equal(first, second);
        var keys = JsonNode.Parse(first)!.AsObject().Select(p => p.Key).ToList();
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
        var rules = JsonNode.Parse(first)!["rules"]!.AsObject().Select(p => p.Key).ToList();
        Assert.Equal(["OFR0001", "OFR3210", "packs"], rules);
        SchemaAssert.Valid("config", first);
    }

    [Fact]
    public void The_embedded_schema_is_the_file_in_schemas_v1()
    {
        var onDisk = File.ReadAllText(RepositoryFiles.Path("schemas", "v1", "config.json"));
        Assert.Equal(onDisk.ReplaceLineEndings("\n"), ConfigSchema.Text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Defaults_validate_against_the_schema()
    {
        SchemaAssert.Valid("config", OfframpJson.Format(ConfigLoader.Defaults()));
    }

    [Theory]
    [InlineData("OFFRAMP_VERIFY__TIMEOUT_SECONDS", "/verify/timeoutSeconds")]
    [InlineData("OFFRAMP_TARGET", "/target")]
    [InlineData("OFFRAMP_MOVE__TESTS__STRIP_TESTS_SEGMENT", "/move/tests/stripTestsSegment")]
    [InlineData("OFFRAMP_LLM__API_KEY_ENV", "/llm/apiKeyEnv")]
    [InlineData("OFFRAMP_", null)]
    public void Environment_names_map_to_pointers(string name, string? pointer) =>
        Assert.Equal(pointer, ConfigLoader.EnvironmentPointer(name));
}

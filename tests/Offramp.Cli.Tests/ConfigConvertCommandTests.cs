using System.Text.Json.Nodes;
using Offramp.Fixtures;

namespace Offramp.Cli.Tests;

/// <summary><c>config convert</c> on the <c>legacy-csproj</c> fixture's App.config, and the shim on the <c>codemods</c> fixture.</summary>
public sealed class ConfigConvertCommandTests
{
    [Fact]
    [ProducesDiagnostic("OFR4404")]
    [ProducesDiagnostic("OFR4406")]
    public async Task App_config_becomes_appsettings_json_options_classes_and_an_environment_file()
    {
        var fixture = await ScannedFixtures.ScanAsync("legacy-csproj");
        using var repository = fixture.Repository;
        repository.Directory.Write("offramp.yml", "version: 1\n");
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();

        var dryRun = await cli.RunAsync("config", "convert", "--project", "Billing.Tool", "--json");

        Assert.Equal(0, dryRun.ExitCode);
        SchemaAssert.ValidEnvelope(dryRun.Out, "config-convert");
        Assert.False(repository.Directory.Exists("src/Billing.Tool/appsettings.json"));
        await Verify(Scrub.Envelope(dryRun.Out, repository.Path), extension: "json");

        var applied = await cli.RunAsync("config", "convert", "--project", "Billing.Tool", "--apply", "--json");
        var again = await cli.RunAsync("config", "convert", "--project", "Billing.Tool", "--apply", "--json");

        Assert.Equal(0, applied.ExitCode);
        Assert.Contains("\"Retries\": 3", repository.Directory.Read("src/Billing.Tool/appsettings.json"), StringComparison.Ordinal);
        Assert.Contains("\"Region\": \"eu-west\"", repository.Directory.Read("src/Billing.Tool/appsettings.Release.json"), StringComparison.Ordinal);
        Assert.Contains("public class BillingOptions", repository.Directory.Read("src/Billing.Tool/ConfigurationOptions.cs"), StringComparison.Ordinal);
        Assert.Equal(2, again.ExitCode);
        Assert.Contains("\"code\": \"OFR4406\"", again.Out, StringComparison.Ordinal);
    }

    [Fact]
    [ProducesDiagnostic("OFR4401")]
    [ProducesDiagnostic("OFR4402")]
    [ProducesDiagnostic("OFR4403")]
    [ProducesDiagnostic("OFR4405")]
    public async Task Sections_without_a_json_form_are_reported_and_left_out()
    {
        var fixture = await ScannedFixtures.ScanAsync("legacy-csproj", (root, request) =>
        {
            var config = Path.Combine(root, "src/Billing.Tool/App.config");
            File.WriteAllText(config, File.ReadAllText(config)
                .Replace("<section name=\"billing\"", "<section name=\"reports\" type=\"Contoso.Reports.ReportsSection, Reports\" />\n    <section name=\"billing\"", StringComparison.Ordinal)
                .Replace("<billing currency=\"EUR\" retries=\"3\"", "<reports path=\"C:\\Reports\" />\n  <legacyFeature enabled=\"true\" />\n  <system.serviceModel>\n    <client />\n  </system.serviceModel>\n  <system.web>\n    <compilation debug=\"true\" />\n  </system.web>\n  <billing currency=\"EUR\" retries=\"three\"", StringComparison.Ordinal));
            return request;
        });
        using var repository = fixture.Repository;
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();

        var run = await cli.RunAsync("config", "convert", "--project", "Billing.Tool", "--apply", "--json");
        var none = await cli.RunAsync("config", "convert", "--project", "Billing", "--json");

        Assert.Equal(0, run.ExitCode);
        var node = JsonNode.Parse(run.Out)!;
        var messages = node["diagnostics"]!.AsArray().Select(d => d!["code"]!.GetValue<string>() + " " + d["message"]!.GetValue<string>()).ToList();
        Assert.Contains(messages, m => m.StartsWith("OFR4401", StringComparison.Ordinal) && m.Contains("Contoso.Reports.ReportsSection", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("OFR4401", StringComparison.Ordinal) && m.Contains("legacyFeature", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("OFR4401", StringComparison.Ordinal) && m.Contains("billing/retries", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("OFR4402", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("OFR4403", StringComparison.Ordinal));
        var json = repository.Directory.Read("src/Billing.Tool/appsettings.json");
        Assert.DoesNotContain("\"Retries\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Reports\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Currency\": \"EUR\"", json, StringComparison.Ordinal);
        Assert.Equal(2, none.ExitCode);
        Assert.Contains("\"code\": \"OFR4405\"", none.Out, StringComparison.Ordinal);
    }

    /// <summary>--shim end to end: the shim, then the config-manager-shim codemod for the sites config-manager cannot inject into, verified by a build.</summary>
    [Fact]
    public async Task The_shim_takes_the_config_reads_that_cannot_be_injected()
    {
        var fixture = await ScannedFixtures.ScanAsync("codemods", (root, request) =>
        {
            File.WriteAllText(Path.Combine(root, "src/Shop/App.config"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <appSettings>\n    <add key=\"SmtpHost\" value=\"mail.contoso.com\" />\n    <add key=\"Sender\" value=\"shop@contoso.com\" />\n  </appSettings>\n</configuration>\n");
            return request;
        });
        using var repository = fixture.Repository;
        repository.Directory.Write("offramp.yml", "version: 1\n");
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();

        var convert = await cli.RunAsync("config", "convert", "--project", "Shop", "--shim", "--apply", "--json");
        Assert.True(convert.ExitCode == 0, convert.Out);
        var project = repository.Directory.Read("src/Shop/Shop.csproj");
        Assert.Contains("<PackageReference Include=\"Microsoft.Extensions.Configuration.Abstractions\"", project, StringComparison.Ordinal);
        Assert.Contains("<None Update=\"appsettings*.json\"", project, StringComparison.Ordinal);

        var rescan = await cli.RunAsync("scan", "--json");
        Assert.True(rescan.ExitCode == 0, rescan.Out);
        var codemod = await cli.RunAsync("codemod", "run", "--mod", "config-manager-shim", "--apply", "--json");

        Assert.True(codemod.ExitCode == 0, codemod.Out);
        var result = JsonNode.Parse(codemod.Out)!["result"]!;
        Assert.True(result["verify"]!["passed"]!.GetValue<bool>(), codemod.Out);
        var mailer = repository.Directory.Read("src/Shop/Mailer.cs");
        Assert.Contains("return ConfigurationManagerShim.AppSettings[\"Sender\"];", mailer, StringComparison.Ordinal);
        Assert.Contains("return ConfigurationManagerShim.AppSettings[\"SmtpHost\"] + \" at \" + _clock.Now;", mailer, StringComparison.Ordinal);
    }
}

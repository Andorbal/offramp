using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Json;
using Offramp.Core.Progress;
using Offramp.Fixtures;
using Offramp.NuGet.Gac;
using Offramp.Analysis.Rules;
using Offramp.NuGet.Rules;

namespace Offramp.NuGet.Tests;

public sealed class GacTests
{
    [Fact]
    public async Task Usage_counts_come_from_the_compilation_and_tell_unused_references_apart()
    {
        var scanned = await ScannedFixtures.GetAsync("versions");

        var result = GacAnalyzer.Run(scanned.Outcome.Model!, new OfframpConfig(), scanned.Root, null, NullProgressSink.Instance, new DiagnosticBag(), TestContext.Current.CancellationToken);

        var billing = result.Projects.Single(p => p.Project == "src/Billing/Billing.csproj").References.ToDictionary(r => r.Name);
        Assert.True(billing["System.Configuration"].Usages >= 1, "ConfigurationManager is used.");
        Assert.Equal(0, billing["System.Drawing"].Usages);
        Assert.Equal("System.Drawing.Common", billing["System.Drawing"].Mapping.Package);
        Assert.True(billing["System.Drawing"].Mapping.WindowsOnly);
        var web = result.Projects.Single(p => p.Project == "src/Customer.Api/Customer.Api.csproj").References.Single();
        Assert.Equal(FrameworkAssemblyKind.None, web.Mapping.Kind);
        Assert.Equal(2, web.Usages); // HttpUtility and HtmlEncode
        Assert.Equal(1, result.Summary.Unused);
        SchemaAssert.Valid("deps-gac", OfframpJson.Serialize(result, NuGetJsonContext.Default.GacResult));
    }

    [Fact]
    public void Without_a_compiler_log_usages_are_unknown()
    {
        var model = FixtureModels.Load("versions");
        using var empty = new ScratchDirectory("gac");

        var result = GacAnalyzer.Run(model, new OfframpConfig(), empty.Path, "src/Billing/Billing.csproj", NullProgressSink.Instance, new DiagnosticBag(), TestContext.Current.CancellationToken);

        Assert.All(result.Projects.Single().References, r => Assert.Null(r.Usages));
    }

    [Theory]
    [InlineData("System.Core", FrameworkAssemblyKind.Builtin, null)]
    [InlineData("System.Configuration", FrameworkAssemblyKind.Package, "System.Configuration.ConfigurationManager")]
    [InlineData("System.Web.Mvc", FrameworkAssemblyKind.None, null)]
    [InlineData("System.Workflow.Runtime", FrameworkAssemblyKind.None, null)]
    [InlineData("System.Management.Instrumentation", FrameworkAssemblyKind.CompatPack, "Microsoft.Windows.Compatibility")]
    [InlineData("Contoso.Unheard.Of", FrameworkAssemblyKind.Unknown, null)]
    public void Framework_assembly_table(string name, FrameworkAssemblyKind kind, string? package)
    {
        var mapping = FrameworkAssemblyMap.Find(name);

        Assert.Equal(kind, mapping.Kind);
        Assert.Equal(package, mapping.Package);
    }

    [Fact]
    public void Package_map_prefers_exact_ids_then_the_longest_prefix_and_configuration()
    {
        var map = new PackageMap([new PackageMapEntry { Package = "Topshelf", Replacement = "Our hosting template" }, new PackageMapEntry { Prefix = "Contoso.Legacy.", Replacement = "Contoso.Next" }]);

        Assert.Equal("Microsoft.AspNet.WebApi.Core", map.Find("Microsoft.AspNet.WebApi.Core")!.Match);
        Assert.Equal("Microsoft.AspNet.WebApi.*", map.Find("Microsoft.AspNet.WebApi.Owin")!.Match);
        Assert.Equal("Our hosting template", map.Find("Topshelf")!.Replacement);
        Assert.Equal("offramp.yml", map.Find("Contoso.Legacy.Reports")!.Source);
        Assert.Null(map.Find("Newtonsoft.Json"));
    }
}

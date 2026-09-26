using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Configuration;
using Offramp.Analysis.Compilations;
using Offramp.Core.Diagnostics;
using Offramp.Core.Paths;
using Offramp.Fixtures;
using Offramp.Scaffolding.Config;

namespace Offramp.Scaffolding.Tests;

/// <summary>
/// Roadmap M12: <c>config convert</c> round-trips appSettings and a custom section. The
/// generated appsettings.json, read by Microsoft.Extensions.Configuration and bound into the
/// generated options classes (compiled here), holds what App.config holds, read by the
/// section class's property types.
/// </summary>
public sealed class ConfigConvertRoundTripTests
{
    [Fact]
    public async Task App_settings_connection_strings_and_a_custom_section_read_back_the_same()
    {
        var fixture = await ScannedFixtures.GetAsync("legacy-csproj");
        var root = fixture.Repository.Path;
        var project = fixture.Outcome.Model!.Projects.Single(p => p.Id == "src/Billing.Tool/Billing.Tool.csproj");
        using var loader = new CompilationLoader(root);
        var compilation = loader.LoadForProject(project)!;
        var diagnostics = new DiagnosticBag();

        var plan = ConfigConverter.Plan(new ConfigConvertRequest { RepositoryRoot = root, Project = project, Compilation = compilation, Diagnostics = diagnostics })!;

        var files = plan.ChangeSet.Creates.ToDictionary(c => c.Path, c => Encoding.UTF8.GetString(c.Content));
        var configuration = Configuration(files["src/Billing.Tool/appsettings.json"]);
        var xml = XDocument.Load(RepoPaths.ToAbsolute(root, "src/Billing.Tool/App.config")).Root!;

        foreach (var add in xml.Element("appSettings")!.Elements("add"))
        {
            Assert.Equal(add.Attribute("value")!.Value, configuration[add.Attribute("key")!.Value]);
        }

        foreach (var add in xml.Element("connectionStrings")!.Elements("add"))
        {
            Assert.Equal(add.Attribute("connectionString")!.Value, configuration.GetConnectionString(add.Attribute("name")!.Value));
        }

        var options = Compile(files["src/Billing.Tool/ConfigurationOptions.cs"]);
        var billingType = options.GetType("Contoso.Billing.Tool.BillingOptions", throwOnError: true)!;
        var billing = configuration.GetSection("Billing").Get(billingType)!;
        var schema = SectionSchema.From(compilation.GetTypeByMetadataName("Contoso.Billing.BillingSection")!);
        AssertSameAsXml(schema, xml.Element("billing")!, billing);

        var appSettings = configuration.Get(options.GetType("Contoso.Billing.Tool.AppSettingsOptions", throwOnError: true)!)!;
        Assert.Equal("INV", appSettings.GetType().GetProperty("InvoicePrefix")!.GetValue(appSettings));

        // The Release transform overrides on top of the base file, as ASPNETCORE_ENVIRONMENT=Release would.
        var release = Configuration(files["src/Billing.Tool/appsettings.json"], files["src/Billing.Tool/appsettings.Release.json"]);
        Assert.Equal(("PRD", "eu-west", "50"), (release["InvoicePrefix"], release["Region"], release["PageSize"]));
        var released = release.GetSection("Billing").Get(billingType)!;
        Assert.Equal(("USD", 5, TimeSpan.FromSeconds(30)), ((string)Property(released, "Currency")!, (int)Property(released, "Retries")!, (TimeSpan)Property(released, "Timeout")!));
    }

    /// <summary>Every property the XML sets equals the bound options value, parsed by the section property's type; unset ones keep the class's default.</summary>
    private static void AssertSameAsXml(SectionSchema schema, XElement element, object bound)
    {
        foreach (var property in schema.Properties)
        {
            var value = Property(bound, property.Name);
            switch (property.Kind)
            {
                case SettingKind.Value when element.Attribute(property.XmlName) is { } attribute:
                    Assert.Equal(Typed(property, attribute.Value), value);
                    break;
                case SettingKind.Element when element.Element(property.XmlName) is { } child:
                    AssertSameAsXml(property.Element!, child, value!);
                    break;
            }
        }
    }

    private static object? Typed(SettingProperty property, string text) => property.ValueType switch
    {
        "string" => text,
        "bool" => bool.Parse(text),
        "int" => int.Parse(text, CultureInfo.InvariantCulture),
        "TimeSpan" => TimeSpan.Parse(text, CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException("No round-trip check for " + property.ValueType),
    };

    private static object? Property(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target);

    private static IConfiguration Configuration(params string[] json)
    {
        var builder = new ConfigurationBuilder();
        foreach (var text in json)
        {
            builder.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(text)));
        }

        return builder.Build();
    }

    /// <summary>The generated classes compiled for this runtime, as a C# 7.3 project would compile them.</summary>
    private static Assembly Compile(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("GeneratedOptions",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp7_3))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        stream.Position = 0;
        return new AssemblyLoadContext("GeneratedOptions", isCollectible: true).LoadFromStream(stream);
    }
}

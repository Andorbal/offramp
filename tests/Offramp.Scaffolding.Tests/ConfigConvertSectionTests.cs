using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Fixtures;
using Offramp.Scaffolding.Config;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Scaffolding.Tests;

/// <summary>
/// <c>config convert</c> on SmartStoreNET's Web.config shapes (field test P2, ADR 0060): sections
/// declared in a <c>sectionGroup</c>, and the sections machine.config declares.
/// </summary>
public sealed class ConfigConvertSectionTests
{
    private const string WebConfig = """
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <configSections>
            <sectionGroup name="shop">
              <section name="features" type="System.Configuration.NameValueSectionHandler, System, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089" />
              <sectionGroup name="payment">
                <section name="limits" type="System.Configuration.SingleTagSectionHandler, System, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089" />
              </sectionGroup>
            </sectionGroup>
            <sectionGroup name="bundleTransformer">
              <section name="core" type="BundleTransformer.Core.Configuration.CoreSettings, BundleTransformer.Core" />
            </sectionGroup>
          </configSections>
          <appSettings>
            <add key="ShopName" value="Contoso" />
          </appSettings>
          <shop>
            <features>
              <add key="Wishlist" value="true" />
              <add key="Compare" value="false" />
            </features>
            <payment>
              <limits maxAmount="500" currency="EUR" />
            </payment>
          </shop>
          <bundleTransformer xmlns="http://tempuri.org/BundleTransformer.Configuration.xsd">
            <core>
              <css defaultMinifier="NullMinifier" />
            </core>
          </bundleTransformer>
          <system.net>
            <connectionManagement>
              <add address="*" maxconnection="100" />
            </connectionManagement>
          </system.net>
          <system.data>
            <DbProviderFactories>
              <add name="SQL Server Compact" invariant="System.Data.SqlServerCe.4.0" description="SQL CE" type="System.Data.SqlServerCe.SqlCeProviderFactory, System.Data.SqlServerCe" />
            </DbProviderFactories>
          </system.data>
          <system.codedom>
            <compilers />
          </system.codedom>
          <system.web.extensions />
          <legacyFeature enabled="true" />
        </configuration>
        """;

    [Fact]
    [ProducesDiagnostic("OFR4407")]
    public void Section_groups_convert_and_machine_config_sections_get_their_own_advice()
    {
        using var scratch = new ScratchDirectory("config-sections");
        scratch.Write("src/Site/Web.config", WebConfig);
        var diagnostics = new DiagnosticBag();

        var plan = ConfigConverter.Plan(new ConfigConvertRequest
        {
            RepositoryRoot = scratch.Path,
            Project = new ProjectInfo { Id = "src/Site/Site.csproj", Name = "Site" },
            Diagnostics = diagnostics,
        })!;

        // The group's sections are read where they are, under the group's key.
        var json = Encoding.UTF8.GetString(plan.ChangeSet.Creates.Single(c => c.Path == "src/Site/appsettings.json").Content);
        var configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();
        Assert.Equal(("true", "false"), (configuration["Shop:Features:Wishlist"], configuration["Shop:Features:Compare"]));
        Assert.Equal(("500", "EUR"), (configuration["Shop:Payment:Limits:maxAmount"], configuration["Shop:Payment:Limits:currency"]));
        var sections = plan.Result.Sections.ToDictionary(s => s.Name, StringComparer.Ordinal);
        Assert.Equal(("custom", "Shop:Features"), (sections["shop/features"].Kind, sections["shop/features"].JsonKey));
        Assert.Equal("Shop:Payment:Limits", sections["shop/payment/limits"].JsonKey);

        var messages = diagnostics.ToSortedList().Select(d => d.Code + " " + d.Message).ToList();

        // A section in a group whose class is not in the solution says so, not "not declared".
        Assert.Contains(messages, m => m.StartsWith("OFR4401", StringComparison.Ordinal) && m.Contains("bundleTransformer/core", StringComparison.Ordinal)
            && m.Contains("BundleTransformer.Core.Configuration.CoreSettings", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, m => m.Contains("bundleTransformer is left out", StringComparison.Ordinal));

        // machine.config's sections: the advice for each, and no "not declared".
        Assert.Contains(messages, m => m.StartsWith("OFR4407", StringComparison.Ordinal) && m.Contains("system.net", StringComparison.Ordinal)
            && m.Contains("MaxConnectionsPerServer", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("OFR4407", StringComparison.Ordinal) && m.Contains("system.data", StringComparison.Ordinal)
            && m.Contains("DbProviderFactories.RegisterFactory", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("OFR4403", StringComparison.Ordinal) && m.Contains("system.web.extensions", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, m => m.Contains("system.codedom", StringComparison.Ordinal));
        Assert.Equal(("unsupported", "dropped"), (sections["system.net"].Kind, sections["system.codedom"].Kind));
        Assert.Contains("Razor", sections["system.codedom"].Notes.Single(), StringComparison.Ordinal);
        var undeclared = messages.Where(m => m.Contains("not declared", StringComparison.Ordinal)).ToList();
        Assert.Single(undeclared);
        Assert.Contains("legacyFeature", undeclared[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// SmartStoreNET's <c>&lt;bundleTransformer xmlns="..."&gt;</c>: the xmlns (for the editor's schema) puts
    /// the section's elements in a namespace, which .NET's configuration system ignores; so does the converter.
    /// </summary>
    [Fact]
    public void A_sections_elements_convert_under_an_xmlns()
    {
        using var scratch = new ScratchDirectory("config-sections");
        scratch.Write("src/Site/Web.config", """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <configSections>
                <sectionGroup name="bundleTransformer">
                  <section name="core" type="Bundles.CoreSettings, Bundles" />
                </sectionGroup>
              </configSections>
              <bundleTransformer xmlns="http://tempuri.org/BundleTransformer.Configuration.xsd">
                <core>
                  <css defaultMinifier="NullMinifier" usePreMinifiedFiles="true" />
                </core>
              </bundleTransformer>
            </configuration>
            """);
        var compilation = CSharpCompilation.Create("Bundles",
            [CSharpSyntaxTree.ParseText("""
                namespace System.Configuration
                {
                    public class ConfigurationElement { }
                    public class ConfigurationSection : ConfigurationElement { }
                    [AttributeUsage(AttributeTargets.Property)]
                    public sealed class ConfigurationPropertyAttribute : Attribute
                    {
                        public ConfigurationPropertyAttribute(string name) { }
                    }
                }

                namespace Bundles
                {
                    using System.Configuration;

                    public class CoreSettings : ConfigurationSection
                    {
                        [ConfigurationProperty("css")]
                        public CssSettings Css => null;
                    }

                    public class CssSettings : ConfigurationElement
                    {
                        [ConfigurationProperty("defaultMinifier")]
                        public string DefaultMinifier => null;

                        [ConfigurationProperty("usePreMinifiedFiles")]
                        public bool UsePreMinifiedFiles => false;
                    }
                }
                """)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location), MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var plan = ConfigConverter.Plan(new ConfigConvertRequest
        {
            RepositoryRoot = scratch.Path,
            Project = new ProjectInfo { Id = "src/Site/Site.csproj", Name = "Site" },
            Compilation = compilation,
            Diagnostics = new DiagnosticBag(),
        })!;

        var json = Encoding.UTF8.GetString(plan.ChangeSet.Creates.Single(c => c.Path == "src/Site/appsettings.json").Content);
        var configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();
        Assert.Equal(("NullMinifier", "True"), (configuration["BundleTransformer:Core:Css:DefaultMinifier"], configuration["BundleTransformer:Core:Css:UsePreMinifiedFiles"]));
        Assert.Equal(2, plan.Result.Sections.Single(s => s.Name == "bundleTransformer/core").Values);
    }

    [Fact]
    public void A_group_name_selects_its_sections()
    {
        using var scratch = new ScratchDirectory("config-sections");
        scratch.Write("src/Site/Web.config", WebConfig);

        var plan = ConfigConverter.Plan(new ConfigConvertRequest
        {
            RepositoryRoot = scratch.Path,
            Project = new ProjectInfo { Id = "src/Site/Site.csproj", Name = "Site" },
            Sections = ["shop"],
            Diagnostics = new DiagnosticBag(),
        })!;

        Assert.Equal(["shop/features", "shop/payment/limits"], plan.Result.Sections.Where(s => s.Kind == "custom").Select(s => s.Name));
    }
}

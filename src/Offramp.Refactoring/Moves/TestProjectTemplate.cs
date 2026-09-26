using System.Text;
using System.Text.Json.Nodes;
using Offramp.Core.Configuration;

namespace Offramp.Refactoring.Moves;

/// <summary>A package the new test project references.</summary>
public sealed record TemplatePackage(string Id, string Version);

/// <summary>What a created test project needs to know.</summary>
public sealed record TestProjectSpec
{
    /// <summary>The test framework (<c>xunit</c>, <c>xunit.v3</c>, <c>nunit</c>, <c>mstest</c>, <c>tunit</c>).</summary>
    public required string Framework { get; init; }

    public required IReadOnlyList<string> TargetFrameworks { get; init; }

    /// <summary>The source project, relative to the new project's folder, with backslashes.</summary>
    public required string SourceReference { get; init; }

    public required IReadOnlyList<TemplatePackage> Packages { get; init; }

    /// <summary>.NET Framework references (<c>Reference Include</c>) copied from the source project.</summary>
    public IReadOnlyList<string> FrameworkReferences { get; init; } = [];

    /// <summary>Language settings copied from the source project (LangVersion, Nullable, ImplicitUsings).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Properties { get; init; } = [];

    /// <summary>Central package management: package references carry no version.</summary>
    public bool CentralVersions { get; init; }
}

/// <summary>
/// New SDK-style test projects (<c>move tests --create</c>). The packages per framework
/// come from <c>rules/test-projects.yml</c>; the framework package keeps the source
/// project's version, since the moved tests compile against it.
/// </summary>
public static class TestProjectTemplate
{
    private static readonly Lazy<JsonNode> Rules = new(() =>
    {
        using var stream = typeof(TestProjectTemplate).Assembly.GetManifestResourceStream("Offramp.Refactoring.Rules.test-projects.yml")
            ?? throw new InvalidOperationException("Missing embedded rule file test-projects.yml.");
        using var reader = new StreamReader(stream);
        return YamlJson.Parse(reader.ReadToEnd()).Root;
    });

    public static IReadOnlyList<string> Frameworks => [.. Rules.Value["frameworks"]!.AsObject().Select(f => f.Key)];

    /// <summary>True when the framework can only target modern .NET (TUnit).</summary>
    public static bool ModernOnly(string framework) =>
        Rules.Value["frameworks"]?[framework]?["modernOnly"]?.GetValue<bool>() == true;

    /// <summary>The framework's packages, with the source project's version for any it already references.</summary>
    public static List<TemplatePackage> Packages(string framework, IReadOnlyDictionary<string, string> sourceVersions)
    {
        var packages = Rules.Value["frameworks"]?[framework]?["packages"]?.AsArray()
            ?? throw new ArgumentOutOfRangeException(nameof(framework), framework, null);
        return
        [
            .. packages.Select(p =>
            {
                var id = p!["id"]!.GetValue<string>();
                return new TemplatePackage(id, sourceVersions.TryGetValue(id, out var version) ? version : p["version"]!.ToString());
            }),
        ];
    }

    public static string Render(TestProjectSpec spec)
    {
        var builder = new StringBuilder();
        builder.Append("<Project Sdk=\"Microsoft.NET.Sdk\">\n\n  <PropertyGroup>\n");
        builder.Append(spec.TargetFrameworks.Count == 1
            ? $"    <TargetFramework>{spec.TargetFrameworks[0]}</TargetFramework>\n"
            : $"    <TargetFrameworks>{string.Join(';', spec.TargetFrameworks)}</TargetFrameworks>\n");
        foreach (var (name, value) in spec.Properties)
        {
            builder.Append("    <").Append(name).Append('>').Append(Escape(value)).Append("</").Append(name).Append(">\n");
        }

        builder.Append("    <IsPackable>false</IsPackable>\n    <IsTestProject>true</IsTestProject>\n  </PropertyGroup>\n\n  <ItemGroup>\n");
        foreach (var package in spec.Packages.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append("    <PackageReference Include=\"").Append(Escape(package.Id)).Append('"');
            if (!spec.CentralVersions)
            {
                builder.Append(" Version=\"").Append(Escape(package.Version)).Append('"');
            }

            builder.Append(" />\n");
        }

        builder.Append("  </ItemGroup>\n\n");
        if (spec.FrameworkReferences.Count > 0)
        {
            builder.Append("  <ItemGroup>\n");
            foreach (var reference in spec.FrameworkReferences.Order(StringComparer.OrdinalIgnoreCase))
            {
                builder.Append("    <Reference Include=\"").Append(Escape(reference)).Append("\" />\n");
            }

            builder.Append("  </ItemGroup>\n\n");
        }

        builder.Append("  <ItemGroup>\n    <ProjectReference Include=\"").Append(Escape(spec.SourceReference)).Append("\" />\n  </ItemGroup>\n\n</Project>\n");
        return builder.ToString();
    }

    private static string Escape(string value) => System.Security.SecurityElement.Escape(value) ?? "";
}

using System.Globalization;
using System.Text;

namespace Offramp.Fixtures;

/// <summary>
/// Fixtures too large to check in, written on demand into a scratch repository
/// (docs/spec/04-testing-and-fixtures.md). The output depends only on the arguments.
/// </summary>
public static class GeneratedFixtures
{
    private const int TypesPerArea = 49;

    /// <summary>
    /// <c>hollow</c>: <c>src/Big</c> (netstandard2.0) with <paramref name="areas"/> folders of 49
    /// public types and one internal helper each (50 files per area), chained so each type uses the
    /// previous one and the helper; <c>src/Big.Core</c>, an empty destination; and <c>src/App</c>,
    /// which references Big and uses its first and last types. Moving every file of Big into
    /// Big.Core is the overnight hollow-out run.
    /// </summary>
    public static void Hollow(string root, int areas = 10)
    {
        Write(root, "global.json", "{\n  \"sdk\": {\n    \"version\": \"10.0.100\",\n    \"rollForward\": \"latestFeature\"\n  }\n}\n");
        Write(root, "Directory.Build.props", "<Project>\n  <PropertyGroup>\n    <NuGetAudit>false</NuGetAudit>\n  </PropertyGroup>\n</Project>\n");
        Write(root, "Directory.Packages.props", "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n</Project>\n");
        Write(root, "Hollow.slnx",
            "<Solution>\n  <Folder Name=\"/src/\">\n    <Project Path=\"src/App/App.csproj\" />\n    <Project Path=\"src/Big.Core/Big.Core.csproj\" />\n    <Project Path=\"src/Big/Big.csproj\" />\n  </Folder>\n</Solution>\n");

        Write(root, "src/Big/Big.csproj", Project("Big"));
        for (var area = 0; area < areas; area++)
        {
            var ns = string.Create(CultureInfo.InvariantCulture, $"Big.Area{area}");
            Write(root, $"src/Big/Area{area}/Helpers.cs",
                $"namespace {ns}\n{{\n    internal static class Helpers\n    {{\n        public static int Step(int value) => value + 1;\n    }}\n}}\n");
            for (var type = 0; type < TypesPerArea; type++)
            {
                var body = type == 0 ? "Helpers.Step(0)" : string.Create(CultureInfo.InvariantCulture, $"Helpers.Step(new Type{type - 1}().Value())");
                Write(root, string.Create(CultureInfo.InvariantCulture, $"src/Big/Area{area}/Type{type}.cs"),
                    string.Create(CultureInfo.InvariantCulture, $"namespace {ns}\n{{\n    public sealed class Type{type}\n    {{\n        public int Value() => {body};\n    }}\n}}\n"));
            }
        }

        Write(root, "src/Big.Core/Big.Core.csproj", Project("Big.Core"));
        Write(root, "src/Big.Core/Marker.cs", "namespace Big.Core\n{\n    internal static class Marker\n    {\n    }\n}\n");

        Write(root, "src/App/App.csproj", Project("App", "../Big/Big.csproj"));
        Write(root, "src/App/Totals.cs", string.Create(CultureInfo.InvariantCulture,
            $"namespace App\n{{\n    public static class Totals\n    {{\n        public static int Sum() => new Big.Area0.Type0().Value() + new Big.Area{areas - 1}.Type{TypesPerArea - 1}().Value();\n    }}\n}}\n"));
    }

    /// <summary>The number of files <see cref="Hollow"/> puts in <c>src/Big</c>.</summary>
    public static int HollowFiles(int areas = 10) => areas * (TypesPerArea + 1);

    private static string Project(string name, string? reference = null)
    {
        var builder = new StringBuilder();
        builder.Append("<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>netstandard2.0</TargetFramework>\n");
        builder.Append("    <RootNamespace>").Append(name).Append("</RootNamespace>\n  </PropertyGroup>\n");
        if (reference is not null)
        {
            builder.Append("  <ItemGroup>\n    <ProjectReference Include=\"").Append(reference).Append("\" />\n  </ItemGroup>\n");
        }

        return builder.Append("</Project>\n").ToString();
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }
}

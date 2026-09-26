using System.Xml.Linq;
using Offramp.Core.Processes;

namespace Offramp.Fixtures;

/// <summary>
/// What <c>nuget restore</c> does for packages.config projects, which the .NET CLI cannot:
/// fills <c>packages/&lt;Id&gt;.&lt;Version&gt;/</c> at the repository root with the extracted
/// packages. The packages come from NuGet's global packages folder, after a throwaway
/// PackageReference restore puts them there.
/// </summary>
public static class PackagesConfigRestore
{
    public static async Task RestoreAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        var packages = Directory.EnumerateFiles(repositoryRoot, "packages.config", SearchOption.AllDirectories)
            .SelectMany(file => XDocument.Load(file).Root!.Elements("package"))
            .Select(p => (Id: p.Attribute("id")!.Value, Version: p.Attribute("version")!.Value))
            .Distinct()
            .ToList();
        if (packages.Count == 0)
        {
            return;
        }

        using var scratch = new ScratchDirectory("packages-config");
        File.WriteAllText(Path.Combine(scratch.Path, "Restore.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>netstandard2.0</TargetFramework>\n  </PropertyGroup>\n  <ItemGroup>\n"
            + string.Concat(packages.Select(p => $"    <PackageReference Include=\"{p.Id}\" Version=\"[{p.Version}]\" />\n"))
            + "  </ItemGroup>\n</Project>\n");
        var restore = await ProcessRunner.Instance.RunAsync(new ProcessSpec("dotnet", ["restore", "Restore.csproj", "-nologo"]) { WorkingDirectory = scratch.Path, Timeout = TimeSpan.FromMinutes(10) }, cancellationToken);
        if (!restore.Succeeded)
        {
            throw new InvalidOperationException("Restoring the packages.config packages failed: " + restore.StandardOutput + restore.StandardError);
        }

        var locals = await ProcessRunner.Instance.RunAsync(new ProcessSpec("dotnet", ["nuget", "locals", "global-packages", "--list"]) { WorkingDirectory = scratch.Path }, cancellationToken);
        var global = locals.StandardOutput.Trim().Split(':', 2)[1].Trim();
        foreach (var (id, version) in packages)
        {
            FixtureRepository.CopyDirectory(Path.Combine(global, id.ToLowerInvariant(), version.ToLowerInvariant()), Path.Combine(repositoryRoot, "packages", $"{id}.{version}"));
        }
    }
}

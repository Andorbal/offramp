using System.Security;
using System.Text;
using System.Text.Json;
using Offramp.Core.Caching;
using Offramp.Core.Processes;

namespace Offramp.Workspace.Targets;

/// <summary>What to compile against: a target framework, shared frameworks, and packages.</summary>
public sealed record TargetReferenceRequest
{
    /// <summary><c>net10.0</c>, or <c>net10.0-windows</c> for desktop projects.</summary>
    public required string TargetFramework { get; init; }

    /// <summary>Shared frameworks beyond Microsoft.NETCore.App: <c>Microsoft.AspNetCore.App</c>, <c>Microsoft.WindowsDesktop.App</c>.</summary>
    public IReadOnlyList<string> Frameworks { get; init; } = [];

    /// <summary>Direct packages (id, exact version); the ones that do not support the target are dropped.</summary>
    public IReadOnlyList<(string Id, string Version)> Packages { get; init; } = [];

    public string Key()
    {
        var text = new StringBuilder(TargetFramework);
        foreach (var framework in Frameworks.Order(StringComparer.Ordinal))
        {
            text.Append('|').Append(framework);
        }

        foreach (var (id, version) in Packages.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
        {
            text.Append('|').Append(id.ToLowerInvariant()).Append('/').Append(version);
        }

        return ContentHash.Sha256(text.ToString())[..16];
    }
}

/// <summary>The reference assemblies for a request, or why there are none.</summary>
public sealed record TargetReferences
{
    /// <summary>Absolute paths, sorted.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>Requested packages that do not support the target (NU1202, directly or through a dependency).</summary>
    public IReadOnlyList<string> DroppedPackages { get; init; } = [];

    /// <summary>Set when NuGet or MSBuild failed for another reason: the reference set is unusable.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Resolves the reference assemblies a project would compile against on the target, with the
/// real toolchain: a scratch project under <c>.offramp/cache/targets/</c> that restores and
/// resolves references (<c>dotnet msbuild -restore -getItem</c>). The SDK chooses the
/// reference packs; NuGet chooses package assets and says which packages do not support the
/// target. Results are cached by request and reused while every path still exists.
/// </summary>
public sealed class TargetReferenceResolver(string repositoryRoot, IProcessRunner runner, ICache cache)
{
    private const string CacheNamespace = "target-references";

    private readonly Dictionary<string, TargetReferences> _resolved = new(StringComparer.Ordinal);

    public async Task<TargetReferences> ResolveAsync(TargetReferenceRequest request, CancellationToken cancellationToken = default)
    {
        var key = request.Key();
        if (_resolved.TryGetValue(key, out var known))
        {
            return known;
        }

        if (cache.TryGet(CacheNamespace, key, out var cached) && Deserialize(cached) is { Error: null } hit && hit.Paths.All(File.Exists))
        {
            return _resolved[key] = hit;
        }

        var result = await ResolveUncachedAsync(request, key, cancellationToken).ConfigureAwait(false);
        if (result.Error is null)
        {
            cache.Set(CacheNamespace, key, JsonSerializer.Serialize(new CachedReferences(result.Paths, result.DroppedPackages)));
        }

        return _resolved[key] = result;
    }

    private async Task<TargetReferences> ResolveUncachedAsync(TargetReferenceRequest request, string key, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(repositoryRoot, ".offramp", "cache", "targets", key);
        Directory.CreateDirectory(directory);

        // Nothing from the repository's own build customization applies; its NuGet.config and
        // global.json still do, so feeds and the SDK are the repository's.
        File.WriteAllText(Path.Combine(directory, "Directory.Build.props"), "<Project />\n");
        File.WriteAllText(Path.Combine(directory, "Directory.Build.targets"), "<Project />\n");
        File.WriteAllText(Path.Combine(directory, "Directory.Packages.props"),
            "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n</Project>\n");

        var packages = request.Packages.ToList();
        var dropped = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var attempt = 0; ; attempt++)
        {
            var project = Path.Combine(directory, "references.csproj");
            File.WriteAllText(project, ProjectText(request, packages));
            var result = await runner.RunAsync(new ProcessSpec("dotnet",
            [
                "msbuild", project, "-restore", "-nologo",
                "-t:FindReferenceAssembliesForReferences", "-getItem:ReferencePathWithRefAssemblies",
            ])
            { WorkingDirectory = directory, Timeout = TimeSpan.FromMinutes(10) }, cancellationToken).ConfigureAwait(false);

            if (result.NotFound)
            {
                return new TargetReferences { Error = "The dotnet CLI could not be started." };
            }

            if (result.Succeeded && ParsePaths(result.StandardOutput) is { Count: > 0 } paths)
            {
                return new TargetReferences { Paths = paths, DroppedPackages = [.. dropped] };
            }

            var output = result.StandardError + "\n" + result.StandardOutput;
            var incompatible = Incompatible(output);
            var drop = incompatible.Count == 0 || attempt > 1 ? [] : DirectPackagesReaching(directory, packages, incompatible);
            if (drop.Count == 0)
            {
                return new TargetReferences { Error = FirstErrors(output) };
            }

            dropped.UnionWith(drop);
            packages = [.. packages.Where(p => !drop.Contains(p.Id, StringComparer.OrdinalIgnoreCase))];
        }
    }

    private static string ProjectText(TargetReferenceRequest request, IReadOnlyList<(string Id, string Version)> packages)
    {
        var text = new StringBuilder();
        text.Append("<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n");
        text.Append("    <TargetFramework>").Append(Escape(request.TargetFramework)).Append("</TargetFramework>\n");
        text.Append("    <DisableImplicitAssetTargetFallback>true</DisableImplicitAssetTargetFallback>\n");
        text.Append("    <EnableDefaultItems>false</EnableDefaultItems>\n");
        text.Append("    <ImplicitUsings>disable</ImplicitUsings>\n");
        if (request.TargetFramework.EndsWith("-windows", StringComparison.Ordinal))
        {
            text.Append("    <EnableWindowsTargeting>true</EnableWindowsTargeting>\n");
        }

        text.Append("  </PropertyGroup>\n  <ItemGroup>\n");
        foreach (var framework in request.Frameworks.Order(StringComparer.Ordinal))
        {
            text.Append("    <FrameworkReference Include=\"").Append(Escape(framework)).Append("\" />\n");
        }

        foreach (var (id, version) in packages.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
        {
            text.Append("    <PackageReference Include=\"").Append(Escape(id)).Append("\" Version=\"[").Append(Escape(version)).Append("]\" />\n");
        }

        text.Append("  </ItemGroup>\n</Project>\n");
        return text.ToString();
    }

    private static string Escape(string value) => SecurityElement.Escape(value);

    private static List<string>? ParsePaths(string output)
    {
        var start = output.IndexOf('{', StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(output[start..]);
            var items = document.RootElement.GetProperty("Items").GetProperty("ReferencePathWithRefAssemblies");
            return [.. items.EnumerateArray()
                .Select(i => i.GetProperty("FullPath").GetString())
                .OfType<string>()
                .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];
        }
        catch (JsonException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Package ids NuGet reports as not supporting the target (NU1202).</summary>
    internal static SortedSet<string> Incompatible(string output)
    {
        const string Marker = "error NU1202: Package ";
        var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n'))
        {
            var at = line.IndexOf(Marker, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            var rest = line[(at + Marker.Length)..];
            var space = rest.IndexOf(' ', StringComparison.Ordinal);
            if (space > 0)
            {
                ids.Add(rest[..space]);
            }
        }

        return ids;
    }

    /// <summary>
    /// The direct packages whose dependency closure (from the assets file NuGet writes even
    /// when restore fails) contains an incompatible package.
    /// </summary>
    private static HashSet<string> DirectPackagesReaching(string directory, IReadOnlyList<(string Id, string Version)> packages, SortedSet<string> incompatible)
    {
        var dependencies = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var assets = Path.Combine(directory, "obj", "project.assets.json");
        if (File.Exists(assets))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(assets));
            if (document.RootElement.TryGetProperty("targets", out var targets))
            {
                foreach (var target in targets.EnumerateObject())
                {
                    foreach (var library in target.Value.EnumerateObject())
                    {
                        var id = library.Name[..library.Name.IndexOf('/', StringComparison.Ordinal)];
                        var list = dependencies.TryGetValue(id, out var existing) ? existing : dependencies[id] = [];
                        if (library.Value.TryGetProperty("dependencies", out var deps))
                        {
                            list.AddRange(deps.EnumerateObject().Select(d => d.Name));
                        }
                    }
                }
            }
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, _) in packages)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>([id]);
            while (queue.TryDequeue(out var current))
            {
                if (!seen.Add(current))
                {
                    continue;
                }

                if (incompatible.Contains(current))
                {
                    result.Add(id);
                    break;
                }

                foreach (var next in dependencies.TryGetValue(current, out var deps) ? deps : [])
                {
                    queue.Enqueue(next);
                }
            }
        }

        return result;
    }

    private static string FirstErrors(string output)
    {
        var errors = output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains(" error ", StringComparison.Ordinal) || l.StartsWith("error ", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToList();
        return errors.Count > 0 ? string.Join("\n", errors) : "Reference resolution produced no reference assemblies.";
    }

    private static TargetReferences? Deserialize(string json)
    {
        try
        {
            var cached = JsonSerializer.Deserialize<CachedReferences>(json);
            return cached is null ? null : new TargetReferences { Paths = cached.Paths, DroppedPackages = cached.DroppedPackages };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record CachedReferences(IReadOnlyList<string> Paths, IReadOnlyList<string> DroppedPackages);
}

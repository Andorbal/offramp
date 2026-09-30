using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Versioning;
using Offramp.Workspace.Environment;

namespace Offramp.Refactoring.Moves;

/// <summary>
/// The facades MSBuild adds to a .NET Framework compilation once it references a .NET Standard
/// assembly: <c>ImplicitlyExpandNETStandardFacades</c> (the SDK's <c>Microsoft.NET.Build.Extensions</c>,
/// with <c>netstandard.dll</c>, for net461 to net471) and <c>ImplicitlyExpandDesignTimeFacades</c> (the
/// reference assemblies' <c>Facades</c> folder, with <c>netstandard.dll</c> from net471 on). A recorded
/// compilation of a project that referenced no .NET Standard assembly has none of them, so a trial
/// compilation that adds such a reference fails with CS0012 ("defined in an assembly that is not
/// referenced ... netstandard") where the real build would not.
/// </summary>
internal static class NetStandardFacades
{
    private static readonly Version First = new(4, 6, 1);
    private static readonly Version Inbox = new(4, 7, 2);
    private static readonly Lazy<string?> Extensions = new(FindBuildExtensions);

    // Found and read once per process: every trial compilation of a plan may need them.
    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> Files = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, MetadataReference> References = new(StringComparer.Ordinal);

    /// <summary>
    /// <paramref name="compilation"/>, which targets <paramref name="tfm"/> and now includes <paramref name="added"/>,
    /// with the facades a build would add; unchanged when it is not .NET Framework 4.6.1 or later, already has
    /// <c>netstandard.dll</c>, none of the added references depends on .NET Standard, or the facades cannot be found.
    /// </summary>
    public static CSharpCompilation Add(CSharpCompilation compilation, string tfm, IEnumerable<MetadataReference> added)
    {
        var framework = NuGetFramework.Parse(tfm);
        if (framework.Framework != FrameworkConstants.FrameworkIdentifiers.Net || framework.Version < First
            || compilation.ReferencedAssemblyNames.Any(n => n.Name == "netstandard")
            || !added.Any(r => DependsOnNetStandard(compilation, r)))
        {
            return compilation;
        }

        var referenced = compilation.ReferencedAssemblyNames.Select(n => n.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var facades = new List<MetadataReference>();
        var files = Files.GetOrAdd(framework.GetShortFolderName(), _ => [.. ExtensionFacades(framework.Version).Concat(DesignTimeFacades(framework))]);
        foreach (var file in files)
        {
            if (referenced.Add(Path.GetFileNameWithoutExtension(file)))
            {
                facades.Add(References.GetOrAdd(file, f => MetadataReference.CreateFromFile(f)));
            }
        }

        return facades.Count == 0 ? compilation : compilation.AddReferences(facades);
    }

    /// <summary>Whether a reference is to an assembly built against .NET Standard (it references <c>netstandard</c> or <c>System.Runtime</c>).</summary>
    private static bool DependsOnNetStandard(CSharpCompilation compilation, MetadataReference reference)
    {
        var names = reference is CompilationReference built
            ? built.Compilation.ReferencedAssemblyNames
            : compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly ? assembly.Modules.SelectMany(m => m.ReferencedAssemblies) : [];
        return names.Any(n => n.Name is "netstandard" or "System.Runtime");
    }

    /// <summary>The SDK's facades for the version, as its <c>Microsoft.NET.Build.Extensions.NETFramework.targets</c> picks them (newest folder first, one file per name).</summary>
    private static IEnumerable<string> ExtensionFacades(Version version)
    {
        if (version >= Inbox || Extensions.Value is not { } root)
        {
            return [];
        }

        string[] folders = version >= new Version(4, 7, 1) ? ["net471"]
            : version >= new Version(4, 7) ? ["net47", "net462", "net461"]
            : version >= new Version(4, 6, 2) ? ["net462", "net461"]
            : ["net461"];
        return folders
            .Select(f => Path.Combine(root, f, "lib"))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.dll").Order(StringComparer.Ordinal))
            .DistinctBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The <c>Facades</c> folder of the reference assemblies for the target, where MSBuild looks for them: the
    /// <c>Microsoft.NETFramework.ReferenceAssemblies</c> package the SDK restores (newest version in the global
    /// packages folder), else the targeting pack Visual Studio installs. The recorded compilation cannot say: the
    /// compiler log keeps its references' file names only.
    /// </summary>
    private static IEnumerable<string> DesignTimeFacades(NuGetFramework framework)
    {
        var version = "v" + framework.Version.ToString(framework.Version.Build > 0 ? 3 : 2);
        var package = Path.Combine(GlobalPackagesFolder(), "microsoft.netframework.referenceassemblies." + framework.GetShortFolderName());
        var packaged = Directory.Exists(package)
            ? Directory.EnumerateDirectories(package)
                .Where(d => NuGetVersion.TryParse(Path.GetFileName(d), out _))
                .OrderByDescending(d => NuGetVersion.Parse(Path.GetFileName(d)))
                .Select(d => Path.Combine(d, "build", ".NETFramework", version, "Facades"))
            : [];
        var installed = System.Environment.GetEnvironmentVariable("ProgramFiles(x86)") is { Length: > 0 } programFiles
            ? [Path.Combine(programFiles, "Reference Assemblies", "Microsoft", "Framework", ".NETFramework", version, "Facades")]
            : Array.Empty<string>();
        return packaged.Concat(installed).FirstOrDefault(Directory.Exists) is { } facades
            ? Directory.EnumerateFiles(facades, "*.dll").Order(StringComparer.Ordinal)
            : [];
    }

    private static string GlobalPackagesFolder() =>
        SettingsUtility.GetGlobalPackagesFolder(Settings.LoadDefaultSettings(null));

    /// <summary>
    /// <c>sdk/VERSION/Microsoft/Microsoft.NET.Build.Extensions</c> of the newest SDK installed with the
    /// running .NET (its files have not changed since .NET Core 2.0), or null.
    /// </summary>
    private static string? FindBuildExtensions()
    {
        var sdks = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "sdk"));
        return Directory.Exists(sdks)
            ? Directory.EnumerateDirectories(sdks)
                .OrderByDescending(d => Path.GetFileName(d), SdkVersionComparer.Instance)
                .Select(d => Path.Combine(d, "Microsoft", "Microsoft.NET.Build.Extensions"))
                .FirstOrDefault(Directory.Exists)
            : null;
    }
}

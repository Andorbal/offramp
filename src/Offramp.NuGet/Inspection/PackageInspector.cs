using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using NuGet.Frameworks;
using NuGet.Packaging;

namespace Offramp.NuGet.Inspection;

/// <summary>
/// Reads a nupkg's layout (docs/spec/commands/deps.md, "Determining supports target"):
/// the frameworks its assets are for, and which assemblies are Windows-only.
/// </summary>
public static class PackageInspector
{
    /// <summary>Referencing any of these makes an assembly Windows-only on modern .NET.</summary>
    public static readonly IReadOnlyList<string> WindowsOnlyReferences =
    [
        "System.Windows.Forms", "PresentationFramework", "PresentationCore", "System.Web",
        "System.Drawing", "Microsoft.Win32.Registry", "System.DirectoryServices",
    ];

    private static readonly string[] AssetRoots = ["lib", "ref", "build", "buildTransitive"];

    public static PackageInspection Inspect(byte[] nupkg)
    {
        using var stream = new MemoryStream(nupkg);
        using var reader = new PackageArchiveReader(stream);
        var identity = reader.GetIdentity();
        var frameworks = new HashSet<NuGetFramework>();
        var assemblies = new List<InspectedAssembly>();
        foreach (var path in reader.GetFiles().Order(StringComparer.Ordinal))
        {
            var framework = AssetFramework(path);
            if (framework is null)
            {
                continue;
            }

            frameworks.Add(framework);
            var isAssembly = path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            var isLibrary = path.StartsWith("lib/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("ref/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase);
            if (isAssembly && isLibrary)
            {
                using var entry = reader.GetStream(path);
                using var copy = new MemoryStream();
                entry.CopyTo(copy);
                assemblies.Add(new InspectedAssembly(path, framework.GetShortFolderName(), WindowsEvidence(copy.ToArray())));
            }
        }

        return new PackageInspection
        {
            Id = identity.Id,
            Version = identity.Version.ToNormalizedString(),
            AssetFrameworks = Names(frameworks),
            DependencyFrameworks = Names(reader.GetPackageDependencies().Select(g => g.TargetFramework)),
            Assemblies = assemblies,
        };
    }

    /// <summary>The framework a package file is an asset for, or null when it is not a framework-specific asset.</summary>
    internal static NuGetFramework? AssetFramework(string path)
    {
        var parts = path.Split('/');
        string? folder = null;
        if (parts.Length >= 3 && AssetRoots.Contains(parts[0], StringComparer.OrdinalIgnoreCase))
        {
            folder = parts[1];
        }
        else if (parts.Length == 2 && parts[0].Equals("lib", StringComparison.OrdinalIgnoreCase))
        {
            // Files directly under lib/ are for the .NET Framework (NuGet's legacy rule).
            return FrameworkConstants.CommonFrameworks.Net11;
        }
        else if (parts.Length >= 5 && parts[0].Equals("runtimes", StringComparison.OrdinalIgnoreCase) && parts[2].Equals("lib", StringComparison.OrdinalIgnoreCase))
        {
            folder = parts[3];
        }
        else if (parts.Length >= 4 && parts[0].Equals("contentFiles", StringComparison.OrdinalIgnoreCase))
        {
            folder = parts[2];
        }

        if (folder is null)
        {
            return null;
        }

        var framework = NuGetFramework.ParseFolder(folder);
        return framework.IsUnsupported ? null : framework;
    }

    /// <summary>Why an assembly only works on Windows, or null.</summary>
    internal static string? WindowsEvidence(byte[] bytes)
    {
        try
        {
            using var pe = new PEReader(new MemoryStream(bytes));
            if (!pe.HasMetadata)
            {
                return null;
            }

            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly)
            {
                return null;
            }

            foreach (var platform in SupportedPlatforms(metadata))
            {
                if (platform.StartsWith("windows", StringComparison.OrdinalIgnoreCase))
                {
                    return $"[SupportedOSPlatform(\"{platform}\")]";
                }
            }

            var references = metadata.AssemblyReferences
                .Select(h => metadata.GetString(metadata.GetAssemblyReference(h).Name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var windowsReference = WindowsOnlyReferences.FirstOrDefault(references.Contains);
            return windowsReference is null ? null : "references " + windowsReference;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    private static IEnumerable<string> SupportedPlatforms(MetadataReader metadata)
    {
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
            {
                continue;
            }

            var parent = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
            if (parent.Kind != HandleKind.TypeReference
                || metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)parent).Name) != "SupportedOSPlatformAttribute")
            {
                continue;
            }

            var blob = metadata.GetBlobReader(attribute.Value);
            if (blob.Length >= 2 && blob.ReadUInt16() == 1 && blob.ReadSerializedString() is { } platform)
            {
                yield return platform;
            }
        }
    }

    private static List<string> Names(IEnumerable<NuGetFramework> frameworks) =>
        [.. frameworks.Where(f => !f.IsUnsupported).Select(f => f.IsAny ? "any" : f.GetShortFolderName()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}

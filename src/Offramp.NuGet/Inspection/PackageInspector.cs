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
    /// <summary>
    /// Referencing any of these makes an assembly Windows-only on modern .NET. Not
    /// <c>Microsoft.Win32.Registry</c>: it is part of the shared framework on every OS, and libraries
    /// reference it for code paths they guard (NUnit runs on Linux).
    /// </summary>
    public static readonly IReadOnlyList<string> WindowsOnlyReferences =
    [
        "System.Windows.Forms", "PresentationFramework", "PresentationCore", "System.Web",
        "System.Drawing", "System.DirectoryServices",
    ];

    /// <summary>
    /// Native libraries that exist only on Windows and that portable code does not call behind an
    /// operating-system check the way it calls kernel32, ntdll, advapi32, the COM runtime (ole32,
    /// oleaut32; ClearScript), or the C runtime: calling one by P/Invoke makes an assembly
    /// Windows-only (DeltaCompressionDotNet calls msdelta.dll).
    /// </summary>
    public static readonly IReadOnlySet<string> WindowsOnlyLibraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "comctl32", "comdlg32", "credui", "cfgmgr32", "dwmapi", "gdi32", "gdiplus", "hid", "imm32", "mpr", "msdelta", "msi",
        "mspatcha", "netapi32", "odbc32", "oleacc", "powrprof", "setupapi", "shell32", "shlwapi",
        "urlmon", "user32", "uxtheme", "wevtapi", "winhttp", "wininet", "winmm", "winscard", "winspool.drv", "wintrust",
        "wlanapi", "wtsapi32",
    };

    private static readonly string[] AssetRoots = ["lib", "ref", "build", "buildTransitive"];

    public static PackageInspection Inspect(byte[] nupkg)
    {
        using var stream = new MemoryStream(nupkg);
        using var reader = new PackageArchiveReader(stream);
        var identity = reader.GetIdentity();
        var frameworks = new HashSet<NuGetFramework>();
        var assemblies = new List<InspectedAssembly>();
        var native = new List<string>();
        foreach (var path in reader.GetFiles().Order(StringComparer.Ordinal))
        {
            if (IsNativeAsset(path))
            {
                native.Add(path);
                continue;
            }

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
                var bytes = copy.ToArray();
                var facts = AssemblyFacts.Read(bytes);
                assemblies.Add(new InspectedAssembly(path, framework.GetShortFolderName(), WindowsEvidence(bytes))
                {
                    Name = facts?.Name,
                    Version = facts?.Version,
                    PublicKeyToken = facts?.PublicKeyToken,
                    FileVersion = facts?.FileVersion,
                    InformationalVersion = facts?.InformationalVersion,
                    Sha256 = facts?.Sha256,
                });
            }
        }

        return new PackageInspection
        {
            Id = identity.Id,
            Version = identity.Version.ToNormalizedString(),
            AssetFrameworks = Names(frameworks),
            DependencyFrameworks = Names(reader.GetPackageDependencies().Select(g => g.TargetFramework)),
            Assemblies = assemblies,
            NativeAssets = native,
            DependencyGroups = [.. reader.GetPackageDependencies()
                .Where(g => !g.TargetFramework.IsUnsupported)
                .Select(g => new InspectedDependencyGroup(
                    g.TargetFramework.IsAny ? "any" : g.TargetFramework.GetShortFolderName(),
                    [.. g.Packages.OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase).Select(d => new InspectedDependency(d.Id, d.VersionRange.ToNormalizedString()))]))
                .OrderBy(g => g.Framework, StringComparer.Ordinal)],
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

    /// <summary>True for a file under <c>runtimes/&lt;rid&gt;/native/</c>: native code for one runtime identifier.</summary>
    internal static bool IsNativeAsset(string path)
    {
        var parts = path.Split('/');
        return parts.Length >= 4
            && parts[0].Equals("runtimes", StringComparison.OrdinalIgnoreCase)
            && parts[2].Equals("native", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The public key token: the last eight bytes of the key's SHA-1, reversed; null for an unsigned assembly.</summary>
    public static string? PublicKeyToken(byte[] publicKey)
    {
        if (publicKey.Length == 0)
        {
            return null;
        }

#pragma warning disable CA5350 // SHA-1 is how .NET defines a public key token, not a security use.
        var hash = System.Security.Cryptography.SHA1.HashData(publicKey);
#pragma warning restore CA5350
        return string.Concat(hash[^8..].Reverse().Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
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
            if (windowsReference is not null)
            {
                return "references " + windowsReference;
            }

            if (NativeLibraries(metadata).FirstOrDefault(WindowsOnlyLibraries.Contains) is { } library)
            {
                return $"calls {library}{(library.Contains('.', StringComparison.Ordinal) ? "" : ".dll")} (P/Invoke)";
            }

            return ComClasses(metadata).FirstOrDefault() is { } com ? $"creates the COM class {com} ([ComImport])" : null;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>The libraries the assembly's P/Invoke methods import, lowercase, without ".dll", sorted.</summary>
    private static List<string> NativeLibraries(MetadataReader metadata)
    {
        var libraries = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var handle in metadata.MethodDefinitions)
        {
            var method = metadata.GetMethodDefinition(handle);
            if ((method.Attributes & System.Reflection.MethodAttributes.PinvokeImpl) == 0)
            {
                continue;
            }

            var import = method.GetImport();
            if (import.Module.IsNil)
            {
                continue;
            }

            var name = metadata.GetString(metadata.GetModuleReference(import.Module).Name).ToLowerInvariant();
            libraries.Add(name.EndsWith(".dll", StringComparison.Ordinal) ? name[..^4] : name);
        }

        return [.. libraries];
    }

    /// <summary>
    /// The full names of the classes the assembly declares with <c>[ComImport]</c> (coclasses, which
    /// create a registered Windows component), sorted. A <c>[ComImport]</c> interface alone is not
    /// evidence: portable libraries declare them for code they guard (EPPlus's IEnumSTATSTG).
    /// </summary>
    private static List<string> ComClasses(MetadataReader metadata) =>
        [.. metadata.TypeDefinitions
            .Select(metadata.GetTypeDefinition)
            .Where(t => (t.Attributes & System.Reflection.TypeAttributes.Import) != 0 && (t.Attributes & System.Reflection.TypeAttributes.Interface) == 0)
            .Select(t => (metadata.GetString(t.Namespace) is { Length: > 0 } ns ? ns + "." : "") + metadata.GetString(t.Name))
            .Order(StringComparer.Ordinal)];

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

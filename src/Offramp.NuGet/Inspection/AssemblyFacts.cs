using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Offramp.NuGet.Inspection;

/// <summary>
/// What identifies one build of an assembly, read with System.Reflection.Metadata (never
/// loaded): its identity, the version attributes the compiler also writes into the Win32
/// version resource, the target framework, and the file's SHA-256. The attributes are read
/// from metadata on every OS, so the answer does not depend on where Offramp runs.
/// </summary>
public sealed record AssemblyFacts
{
    /// <summary>The public key token of the .NET Framework's <c>mscorlib</c>.</summary>
    public const string FrameworkCorlibToken = "b77a5c561934e089";

    private static readonly HashSet<string> StringAttributes = new(StringComparer.Ordinal)
    {
        "System.Reflection.AssemblyFileVersionAttribute",
        "System.Reflection.AssemblyInformationalVersionAttribute",
        "System.Runtime.Versioning.TargetFrameworkAttribute",
        "System.Runtime.InteropServices.ImportedFromTypeLibAttribute",
    };

    public required string Name { get; init; }

    public required string Version { get; init; }

    /// <summary>The public key token (lowercase hex), or null for an unsigned assembly.</summary>
    public string? PublicKeyToken { get; init; }

    /// <summary>The <c>AssemblyFileVersionAttribute</c> value, or null.</summary>
    public string? FileVersion { get; init; }

    /// <summary>The <c>AssemblyInformationalVersionAttribute</c> value, or null.</summary>
    public string? InformationalVersion { get; init; }

    /// <summary>The <c>TargetFrameworkAttribute</c> value (<c>.NETFramework,Version=v4.5</c>), or null.</summary>
    public string? TargetFramework { get; init; }

    /// <summary>
    /// The version of the .NET Framework's <c>mscorlib</c> (public key token
    /// <see cref="FrameworkCorlibToken"/>) the assembly references, or null when it references
    /// none: how a .NET Framework assembly built before <c>TargetFrameworkAttribute</c> (.NET 4.0) shows its framework.
    /// </summary>
    public string? FrameworkCorlib { get; init; }

    /// <summary>
    /// The type library an interop assembly was generated from (<c>ImportedFromTypeLibAttribute</c>,
    /// which tlbimp writes), or null: such an assembly is COM interop, not a library with a package.
    /// </summary>
    public string? ImportedFromTypeLib { get; init; }

    /// <summary>The SHA-256 of the file (lowercase hex).</summary>
    public required string Sha256 { get; init; }

    /// <summary>
    /// The .NET Framework the assembly is built for: its <c>TargetFrameworkAttribute</c> when it
    /// has one, else inferred from the <c>mscorlib</c> it references (v1.0, v1.1, v2.0, or v4.0);
    /// null when it is neither.
    /// </summary>
    public string? InferredFramework => TargetFramework ?? (System.Version.TryParse(FrameworkCorlib, out var corlib) ? FromCorlib(corlib) : null);

    /// <summary>The facts of a file, or null when it cannot be read or is not an assembly.</summary>
    public static AssemblyFacts? ReadFile(string path)
    {
        try
        {
            return Read(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The facts of an assembly's bytes, or null when they are not an assembly.</summary>
    public static AssemblyFacts? Read(byte[] bytes)
    {
        try
        {
            using var pe = new PEReader(new MemoryStream(bytes));
            if (!pe.HasMetadata || !pe.GetMetadataReader().IsAssembly)
            {
                return null;
            }

            var metadata = pe.GetMetadataReader();
            var definition = metadata.GetAssemblyDefinition();
            var attributes = Attributes(metadata);
            return new AssemblyFacts
            {
                Name = metadata.GetString(definition.Name),
                Version = definition.Version.ToString(),
                PublicKeyToken = PackageInspector.PublicKeyToken(metadata.GetBlobBytes(definition.PublicKey)),
                FileVersion = attributes.GetValueOrDefault("System.Reflection.AssemblyFileVersionAttribute"),
                InformationalVersion = attributes.GetValueOrDefault("System.Reflection.AssemblyInformationalVersionAttribute"),
                TargetFramework = attributes.GetValueOrDefault("System.Runtime.Versioning.TargetFrameworkAttribute"),
                ImportedFromTypeLib = attributes.GetValueOrDefault("System.Runtime.InteropServices.ImportedFromTypeLibAttribute"),
                FrameworkCorlib = FrameworkCorlibVersion(metadata),
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            };
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>The .NET Framework version whose <c>mscorlib</c> has this assembly version.</summary>
    private static string FromCorlib(Version corlib) => ".NETFramework,Version=v" + (corlib.Major, corlib.Build) switch
    {
        (1, >= 5000) => "1.1",
        (1, _) => "1.0",
        _ => corlib.Major.ToString(CultureInfo.InvariantCulture) + ".0",
    };

    private static string? FrameworkCorlibVersion(MetadataReader metadata)
    {
        foreach (var handle in metadata.AssemblyReferences)
        {
            var reference = metadata.GetAssemblyReference(handle);
            if (metadata.GetString(reference.Name) == "mscorlib"
                && string.Equals(Token(metadata.GetBlobBytes(reference.PublicKeyOrToken), reference.Flags), FrameworkCorlibToken, StringComparison.Ordinal))
            {
                return reference.Version.ToString();
            }
        }

        return null;
    }

    private static string? Token(byte[] keyOrToken, System.Reflection.AssemblyFlags flags) =>
        (flags & System.Reflection.AssemblyFlags.PublicKey) != 0
            ? PackageInspector.PublicKeyToken(keyOrToken)
            : keyOrToken.Length == 0 ? null : Convert.ToHexString(keyOrToken).ToLowerInvariant();

    /// <summary>The string argument of each of <see cref="StringAttributes"/> the assembly has, by full name (the first wins).</summary>
    private static Dictionary<string, string> Attributes(MetadataReader metadata)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
            {
                continue;
            }

            var parent = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
            if (parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            var type = metadata.GetTypeReference((TypeReferenceHandle)parent);
            var name = metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
            var blob = metadata.GetBlobReader(attribute.Value);
            if (!StringAttributes.Contains(name) || values.ContainsKey(name) || blob.Length < 2 || blob.ReadUInt16() != 1)
            {
                continue;
            }

            try
            {
                if (blob.ReadSerializedString() is { } value)
                {
                    values[name] = value;
                }
            }
            catch (BadImageFormatException)
            {
                // Not a string argument.
            }
        }

        return values;
    }
}

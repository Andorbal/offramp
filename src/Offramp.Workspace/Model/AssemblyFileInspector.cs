using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Offramp.Core.Model;

namespace Offramp.Workspace.Model;

/// <summary>Reads identity metadata from an assembly file with System.Reflection.Metadata (never loads it).</summary>
public static class AssemblyFileInspector
{
    public static AssemblyFileMetadata? Inspect(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                return null;
            }

            var reader = pe.GetMetadataReader();
            if (!reader.IsAssembly)
            {
                return null;
            }

            var definition = reader.GetAssemblyDefinition();
            return new AssemblyFileMetadata
            {
                AssemblyVersion = definition.Version.ToString(),
                TargetFramework = TargetFrameworkAttribute(reader),
                PublicKeyToken = PublicKeyToken(reader, definition),
            };
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? PublicKeyToken(MetadataReader reader, AssemblyDefinition definition)
    {
        if (definition.PublicKey.IsNil)
        {
            return null;
        }

        var key = reader.GetBlobBytes(definition.PublicKey);
#pragma warning disable CA5350 // The public key token is defined as the last 8 bytes of the key's SHA-1; not a security use.
        var hash = SHA1.HashData(key);
#pragma warning restore CA5350
        var token = hash[^8..];
        Array.Reverse(token);
        return Convert.ToHexString(token).ToLowerInvariant();
    }

    private static string? TargetFrameworkAttribute(MetadataReader reader)
    {
        foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
            {
                continue;
            }

            var constructor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            var type = reader.GetTypeReference((TypeReferenceHandle)constructor.Parent);
            if (reader.GetString(type.Name) != "TargetFrameworkAttribute"
                || reader.GetString(type.Namespace) != "System.Runtime.Versioning")
            {
                continue;
            }

            var blob = reader.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() == 1)
            {
                return blob.ReadSerializedString();
            }
        }

        return null;
    }
}

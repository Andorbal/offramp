using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace Offramp.Fixtures.Feeds;

/// <summary>
/// Builds a metadata-only assembly with a recorded identity (name, version, culture,
/// public key), assembly references, and <c>SupportedOSPlatform</c> attributes, and no
/// code. The compiler accepts it as a reference; inspection sees what the real one has.
/// </summary>
public static class StubAssembly
{
    public static byte[] Build(RecordedAssembly assembly)
    {
        var metadata = new MetadataBuilder();
        var identity = Encoding.UTF8.GetBytes(assembly.Name + "|" + assembly.Version + "|" + assembly.PublicKey);
        var mvid = new Guid(SHA256.HashData(identity)[..16]);
        metadata.AddModule(0, metadata.GetOrAddString(assembly.Name + ".dll"), metadata.GetOrAddGuid(mvid), default, default);

        var publicKey = assembly.PublicKey is null ? default : metadata.GetOrAddBlob(Convert.FromHexString(assembly.PublicKey));
        metadata.AddAssembly(
            metadata.GetOrAddString(assembly.Name),
            Version.Parse(assembly.Version),
            assembly.Culture is null ? default : metadata.GetOrAddString(assembly.Culture),
            publicKey,
            assembly.PublicKey is null ? 0 : AssemblyFlags.PublicKey,
            AssemblyHashAlgorithm.Sha1);

        var references = new Dictionary<string, AssemblyReferenceHandle>(StringComparer.Ordinal);
        foreach (var reference in assembly.References)
        {
            references[reference.Name] = metadata.AddAssemblyReference(
                metadata.GetOrAddString(reference.Name),
                Version.Parse(reference.Version),
                default,
                reference.PublicKeyToken is null ? default : metadata.GetOrAddBlob(Convert.FromHexString(reference.PublicKeyToken)),
                default,
                default);
        }

        metadata.AddTypeDefinition(
            default, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        if (assembly.SupportedOSPlatforms.Count > 0)
        {
            if (!references.TryGetValue("System.Runtime", out var runtime))
            {
                runtime = metadata.AddAssemblyReference(
                    metadata.GetOrAddString("System.Runtime"), new Version(8, 0, 0, 0), default,
                    metadata.GetOrAddBlob(Convert.FromHexString("b03f5f7f11d50a3a")), default, default);
            }

            var attributeType = metadata.AddTypeReference(runtime, metadata.GetOrAddString("System.Runtime.Versioning"), metadata.GetOrAddString("SupportedOSPlatformAttribute"));
            var signature = new BlobBuilder();
            new BlobEncoder(signature).MethodSignature(isInstanceMethod: true).Parameters(1, r => r.Void(), p => p.AddParameter().Type().String());
            var constructor = metadata.AddMemberReference(attributeType, metadata.GetOrAddString(".ctor"), metadata.GetOrAddBlob(signature));
            foreach (var platform in assembly.SupportedOSPlatforms)
            {
                var value = new BlobBuilder();
                new BlobEncoder(value).CustomAttributeSignature(
                    fixedArguments => fixedArguments.AddArgument().Scalar().Constant(platform),
                    namedArguments => namedArguments.Count(0));
                metadata.AddCustomAttribute(EntityHandle.AssemblyDefinition, constructor, metadata.GetOrAddBlob(value));
            }
        }

        var builder = new ManagedPEBuilder(
            PEHeaderBuilder.CreateLibraryHeader(),
            new MetadataRootBuilder(metadata),
            new BlobBuilder(),
            flags: CorFlags.ILOnly,
            deterministicIdProvider: content => new BlobContentId(mvid, 0x5A5A5A5A));
        var output = new BlobBuilder();
        builder.Serialize(output);
        return output.ToArray();
    }
}

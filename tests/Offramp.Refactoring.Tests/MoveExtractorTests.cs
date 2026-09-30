using System.Reflection;
using System.Security.Cryptography;
using Offramp.Core.Caching;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Processes;
using Offramp.Fixtures;
using Offramp.Refactoring.Moves;
using Offramp.Workspace.Store;
using Offramp.Workspace.Targets;

namespace Offramp.Refactoring.Tests;

/// <summary><c>move extract</c> from a strong-named project, as NHibernate's.</summary>
public sealed class MoveExtractorTests
{
    [Fact]
    [ProducesDiagnostic("OFR2114")]
    public async Task The_new_project_signs_with_the_source_key()
    {
        using var rsa = new RSACryptoServiceProvider(2048);
        var publicKey = StrongNamePublicKey(rsa.ExportCspBlob(false));
        var fixture = await ScannedFixtures.ScanGeneratedAsync("signed", root => Signed(root, rsa.ExportCspBlob(true), publicKey));
        using var repository = fixture.Repository;
        var model = WorkspaceStore.Read(fixture.WorkspacePath);
        var diagnostics = new DiagnosticBag();

        var extracted = (await MoveExtractor.PlanAsync(new MoveExtractRequest
        {
            RepositoryRoot = fixture.Root,
            Model = model,
            Config = new OfframpConfig(),
            WorkspaceHash = "sha256:" + ContentHash.Sha256File(fixture.WorkspacePath),
            From = "src/Signed/Signed.csproj",
            Files = ["src/Signed/Clock.cs", "src/Signed/Hidden.cs"],
            NewName = "Signed.Core",
            References = new TargetReferenceResolver(fixture.Root, ProcessRunner.Instance, NullCache.Instance),
            Diagnostics = diagnostics,
        }, CancellationToken.None))!;

        Assert.Equal("keys/signing.snk", model.Projects.Single(p => p.Name == "Signed").Properties["AssemblyOriginatorKeyFile"]);
        var result = extracted.Result;
        Assert.Contains("    <SignAssembly>true</SignAssembly>\n    <AssemblyOriginatorKeyFile>..\\..\\keys\\signing.snk</AssemblyOriginatorKeyFile>\n", result.ProjectFile, StringComparison.Ordinal);
        Assert.Equal(["src/Signed/Clock.cs", "src/Signed/Hidden.cs"], result.Plan.Moves.Select(m => m.File));

        // Timer stays and uses the internal Hidden: the grant names the source with its public key.
        var hex = Convert.ToHexString(publicKey).ToLowerInvariant();
        Assert.Equal($"Signed, PublicKey={hex}", result.Plan.ProjectEdits.Single(e => e.Kind == ProjectEditKind.AddInternalsVisibleTo).Value);

        // The source's friends by public key are named; the new project does not grant them.
        var note = Assert.Single(diagnostics.ToSortedList(), d => d.Code == "OFR2114");
        Assert.Contains("Signed.Tests", note.Message, StringComparison.Ordinal);

        // The new project, as the move creates it, builds a strong-named assembly with the same key.
        var changeSet = MoveChangeSet.Build(fixture.Root, result.Plan, new HashSet<string>(StringComparer.Ordinal), out _, extracted.Model, extracted.Created);
        var created = repository.Directory.Combine("src", "Signed.Core");
        Directory.CreateDirectory(created);
        File.WriteAllBytes(Path.Combine(created, "Signed.Core.csproj"), changeSet.Creates.Single().Content);
        File.Copy(repository.Directory.Combine("src", "Signed", "Clock.cs"), Path.Combine(created, "Clock.cs"));
        var build = await ProcessRunner.Instance.RunAsync(new ProcessSpec("dotnet", ["build", "-nologo", "-v:q", "-o", Path.Combine(created, "out")]) { WorkingDirectory = created });
        Assert.True(build.Succeeded, build.StandardOutput + build.StandardError);
        Assert.Equal(publicKey, AssemblyName.GetAssemblyName(Path.Combine(created, "out", "Signed.Core.dll")).GetPublicKey());
    }

    /// <summary>
    /// <c>src/Signed</c> (netstandard2.0), strong-named with <c>keys/signing.snk</c>, granting its
    /// internals to <c>Signed.Tests</c> by public key: Clock (public), Hidden (internal), and Timer,
    /// which uses both. <c>Signed.sln</c> is a Visual Studio 2010 solution with a section the
    /// solution serializer does not know.
    /// </summary>
    private static void Signed(string root, byte[] key, byte[] publicKey)
    {
        static void Write(string root, string path, string text)
        {
            var absolute = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, text);
        }

        Write(root, "global.json", "{\n  \"sdk\": {\n    \"version\": \"10.0.100\",\n    \"rollForward\": \"latestFeature\"\n  }\n}\n");
        Write(root, "Directory.Build.props", "<Project>\n  <PropertyGroup>\n    <NuGetAudit>false</NuGetAudit>\n  </PropertyGroup>\n</Project>\n");
        Write(root, "Directory.Packages.props", "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n</Project>\n");
        Write(root, "Signed.sln", SolutionText);
        Directory.CreateDirectory(Path.Combine(root, "keys"));
        File.WriteAllBytes(Path.Combine(root, "keys", "signing.snk"), key);
        Write(root, "src/Signed/Signed.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>netstandard2.0</TargetFramework>\n    <SignAssembly>true</SignAssembly>\n"
            + "    <AssemblyOriginatorKeyFile>..\\..\\keys\\signing.snk</AssemblyOriginatorKeyFile>\n  </PropertyGroup>\n</Project>\n");
        Write(root, "src/Signed/Friends.cs",
            $"[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"Signed.Tests, PublicKey={Convert.ToHexString(publicKey).ToLowerInvariant()}\")]\n");
        Write(root, "src/Signed/Clock.cs", "namespace Signed\n{\n    public static class Clock\n    {\n        public static System.DateTime Now() => System.DateTime.UtcNow;\n    }\n}\n");
        Write(root, "src/Signed/Hidden.cs", "namespace Signed\n{\n    internal static class Hidden\n    {\n        internal static int Offset => 1;\n    }\n}\n");
        Write(root, "src/Signed/Timer.cs", "namespace Signed\n{\n    public static class Timer\n    {\n        public static long Ticks() => Clock.Now().Ticks + Hidden.Offset;\n    }\n}\n");
    }

    internal const string SolutionText =
        "Microsoft Visual Studio Solution File, Format Version 11.00\r\n"
        + "# Visual Studio 2010\r\n"
        + "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Signed\", \"src\\Signed\\Signed.csproj\", \"{5909BFE7-93CF-4E5F-BE22-6293368AF01D}\"\r\n"
        + "EndProject\r\n"
        + "Global\r\n"
        + "\tGlobalSection(TestCaseManagementSettings) = postSolution\r\n"
        + "\t\tCategoryFile = Signed.vsmdi\r\n"
        + "\tEndGlobalSection\r\n"
        + "\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\r\n"
        + "\t\tDebug|Any CPU = Debug|Any CPU\r\n"
        + "\t\tRelease|Any CPU = Release|Any CPU\r\n"
        + "\tEndGlobalSection\r\n"
        + "\tGlobalSection(ProjectConfigurationPlatforms) = postSolution\r\n"
        + "\t\t{5909BFE7-93CF-4E5F-BE22-6293368AF01D}.Debug|Any CPU.ActiveCfg = Debug|Any CPU\r\n"
        + "\t\t{5909BFE7-93CF-4E5F-BE22-6293368AF01D}.Debug|Any CPU.Build.0 = Debug|Any CPU\r\n"
        + "\t\t{5909BFE7-93CF-4E5F-BE22-6293368AF01D}.Release|Any CPU.ActiveCfg = Release|Any CPU\r\n"
        + "\t\t{5909BFE7-93CF-4E5F-BE22-6293368AF01D}.Release|Any CPU.Build.0 = Release|Any CPU\r\n"
        + "\tEndGlobalSection\r\n"
        + "\tGlobalSection(SolutionProperties) = preSolution\r\n"
        + "\t\tHideSolutionNode = FALSE\r\n"
        + "\tEndGlobalSection\r\n"
        + "EndGlobal\r\n";

    /// <summary>The strong-name public key blob: signature and hash algorithm, length, then the CryptoAPI public key blob as a signature key.</summary>
    private static byte[] StrongNamePublicKey(byte[] cspPublicKey)
    {
        byte[] blob = [.. cspPublicKey];
        blob[5] = 0x24; // CALG_RSA_SIGN, where the key exports as CALG_RSA_KEYX
        return [0x00, 0x24, 0x00, 0x00, 0x04, 0x80, 0x00, 0x00, .. BitConverter.GetBytes(blob.Length), .. blob];
    }
}

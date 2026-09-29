using Offramp.Core.Caching;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Fixtures;
using Offramp.Fixtures.Feeds;
using Offramp.NuGet.Feeds;
using Offramp.Refactoring.Dependencies.Resolution;

namespace Offramp.Refactoring.Tests;

/// <summary>
/// <c>deps resolve-dlls</c> on NHibernate 4.1's checked-in DLLs (field test P0 #4), rebuilt as stubs
/// against a synthetic feed: the package is chosen by the file, the message says what matched, and a
/// Reference's condition survives the swap.
/// </summary>
public sealed class DllResolverTests
{
    private const string IesiKey = "00240000048000009400000006020000002400005253413100040000010001000fc5993f01d68c";
    private const string AntlrKey = "0024000004800000940000000602000000240000525341310004000001000100b1da99af";
    private const string FirebirdKey = "00240000048000009400000006020000002400005253413100040000010001003f3b2a";
    private const string Log4NetOldKey = "002400000480000094000000060200000024000052534131000400000100010097a3e1";
    private const string Log4NetNewKey = "0024000004800000940000000602000000240000525341310004000001000100b32d0f";

    private static readonly RecordedAssembly Iesi4001 = new() { Name = "Iesi.Collections", Version = "4.0.0.0", PublicKey = IesiKey, FileVersion = "4.0.1.4000" };
    private static readonly RecordedAssembly Log4Net1210 = new()
    {
        Name = "log4net", Version = "1.2.10.0", PublicKey = Log4NetOldKey, References = [new RecordedAssemblyReference("mscorlib", "2.0.0.0", "b77a5c561934e089")],
    };

    [Fact]
    [ProducesDiagnostic("OFR1402")]
    public async Task The_package_is_chosen_by_the_file_and_the_message_says_what_matched()
    {
        using var repository = Repository();
        var diagnostics = new DiagnosticBag();

        var plan = await PlanAsync(repository, diagnostics);

        var references = plan.Result.Projects.Single().References.ToDictionary(r => r.Name, StringComparer.Ordinal);
        Assert.Equal(("Iesi.Collections", "4.0.1.4000", DllMatch.Identical), Package(references["Iesi.Collections"]));
        Assert.Equal(("Antlr3.Runtime", "3.5.1", DllMatch.AssemblyVersion), Package(references["Antlr3.Runtime"]));
        Assert.Equal(("FirebirdSql.Data.FirebirdClient", "2.6.5", DllMatch.Newer), Package(references["FirebirdSql.Data.FirebirdClient"]));
        Assert.Equal(("log4net", "1.2.10", DllMatch.Identical), Package(references["log4net"]));
        Assert.Equal("4.0.1.4000", references["Iesi.Collections"].FileVersion);
        var messages = diagnostics.ToSortedList().Where(d => d.Code == "OFR1402").ToDictionary(d => d.File!, d => d.Message, StringComparer.Ordinal);
        Assert.Equal("lib/Iesi.Collections.dll is Iesi.Collections 4.0.1.4000 from package Iesi.Collections 4.0.1.4000: the same file, byte for byte.", messages["lib/Iesi.Collections.dll"]);
        Assert.Contains("no package ships this build; the closest build is in package Antlr3.Runtime 3.5.1", messages["lib/Antlr3.Runtime.dll"], StringComparison.Ordinal);
        Assert.Contains("package FirebirdSql.Data.FirebirdClient 2.6.5 is newer, with FirebirdSql.Data.FirebirdClient 2.6.5.0: an upgrade.", messages["lib/FirebirdSql.Data.FirebirdClient.dll"], StringComparison.Ordinal);
        Assert.Contains("from package log4net 1.2.10 (unlisted): the same file", messages["lib/log4net.dll"], StringComparison.Ordinal);
    }

    [Fact]
    [ProducesDiagnostic("OFR1403")]
    public async Task An_unsigned_dll_is_not_matched_by_its_name_alone()
    {
        using var repository = Repository();
        var diagnostics = new DiagnosticBag();

        var plan = await PlanAsync(repository, diagnostics);

        var dynamic = plan.Result.Projects.Single().References.Single(r => r.Name == "System.Linq.Dynamic");
        Assert.Equal((DllResolutionKind.None, false), (dynamic.Resolution.Kind, dynamic.Blocker));
        var unmatched = Assert.Single(diagnostics.ToSortedList(), d => d.Code == "OFR1403");
        Assert.Contains("it is unsigned, and package System.Linq.Dynamic ships an assembly of that name but not this file or file version", unmatched.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_conditioned_reference_keeps_its_condition()
    {
        using var repository = Repository();

        var plan = await PlanAsync(repository, new DiagnosticBag());

        var after = System.Text.Encoding.UTF8.GetString(plan.ChangeSet!.Edits.Single(e => e.Path == "src/Lib/Lib.csproj").After);
        Assert.Contains("<PackageReference Include=\"Antlr3.Runtime\" Version=\"3.5.1\" Condition=\" '$(Configuration)' == 'Debug' \" />", after, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"Iesi.Collections\" Version=\"4.0.1.4000\" />", after, StringComparison.Ordinal);
        Assert.Contains("<Reference Include=\"System\" />", after, StringComparison.Ordinal);
        Assert.Contains("<ItemGroup Condition=\" '$(Configuration)' == 'Release' \">\n    <PackageReference Include=\"log4net\" Version=\"1.2.10\" />", after, StringComparison.Ordinal);
        Assert.DoesNotContain("HintPath>..\\..\\lib\\Antlr3.Runtime.dll", after, StringComparison.Ordinal);
        Assert.Contains("HintPath>..\\..\\lib\\System.Linq.Dynamic.dll", after, StringComparison.Ordinal);
    }

    private static (string?, string?, DllMatch?) Package(LooseDll dll) => (dll.Resolution.Package, dll.Resolution.Version, dll.Resolution.Match);

    private static Task<ResolveDllsPlan> PlanAsync(ScratchDirectory repository, DiagnosticBag diagnostics, Func<ProjectInfo, ProjectInfo>? customize = null)
    {
        var app = FixtureModels.Load("loose-dlls").Projects.Single(p => p.Name == "App");
        var project = app with
        {
            Id = "src/Lib/Lib.csproj",
            Name = "Lib",
            AssemblyName = "Lib",
            SdkStyle = false,
            TargetFrameworks = ["net40"],
            AssemblyReferences =
            [
                .. new[] { "Antlr3.Runtime", "FirebirdSql.Data.FirebirdClient", "Iesi.Collections", "log4net", "System.Linq.Dynamic" }
                    .Select(n => new AssemblyReferenceInfo { Name = n, HintPath = $"lib/{n}.dll", Kind = AssemblyReferenceKind.File }),
                new AssemblyReferenceInfo { Name = "System", Kind = AssemblyReferenceKind.Framework },
            ],
        };
        return DllResolver.PlanAsync(new ResolveDllsRequest
        {
            RepositoryRoot = repository.Path,
            Model = FixtureModels.Load("loose-dlls") with { Projects = [customize?.Invoke(project) ?? project] },
            Feeds = new RecordedPackageFeeds(Feed()),
            Cache = NullCache.Instance,
            Diagnostics = diagnostics,
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>The checked-in DLLs and a legacy project that references them, one of them only in Debug and one in a Release-only group.</summary>
    private static ScratchDirectory Repository()
    {
        var repository = new ScratchDirectory("dlls");
        void Dll(RecordedAssembly assembly) => File.WriteAllBytes(repository.Write($"lib/{assembly.Name}.dll", ""), StubAssembly.Build(assembly));
        Dll(Iesi4001);
        Dll(Log4Net1210);
        Dll(new RecordedAssembly { Name = "Antlr3.Runtime", Version = "3.5.0.2", PublicKey = AntlrKey, FileVersion = "3.5.0.2" });
        Dll(new RecordedAssembly { Name = "FirebirdSql.Data.FirebirdClient", Version = "2.5.2.0", PublicKey = FirebirdKey, FileVersion = "2.5.2.0" });
        Dll(new RecordedAssembly { Name = "System.Linq.Dynamic", Version = "1.0.0.0" });
        repository.Write("src/Lib/Lib.csproj", """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="4.0" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <TargetFrameworkVersion>v4.0</TargetFrameworkVersion>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="Antlr3.Runtime" Condition=" '$(Configuration)' == 'Debug' ">
                  <HintPath>..\..\lib\Antlr3.Runtime.dll</HintPath>
                </Reference>
                <Reference Include="FirebirdSql.Data.FirebirdClient">
                  <HintPath>..\..\lib\FirebirdSql.Data.FirebirdClient.dll</HintPath>
                </Reference>
                <Reference Include="Iesi.Collections">
                  <HintPath>..\..\lib\Iesi.Collections.dll</HintPath>
                </Reference>
                <Reference Include="System" />
                <Reference Include="System.Linq.Dynamic">
                  <HintPath>..\..\lib\System.Linq.Dynamic.dll</HintPath>
                </Reference>
              </ItemGroup>
              <ItemGroup Condition=" '$(Configuration)' == 'Release' ">
                <Reference Include="log4net">
                  <HintPath>..\..\lib\log4net.dll</HintPath>
                </Reference>
              </ItemGroup>
            </Project>

            """);
        return repository;
    }

    private static FeedRecording Feed()
    {
        static RecordedPackage Package(string id, string version, string folder, RecordedAssembly assembly, bool listed = true) => new()
        {
            Id = id, Version = version, Listed = listed, Synthetic = true,
            Files = [new RecordedFile { Path = $"lib/{folder}/{assembly.Name}.dll", Assembly = assembly }],
        };

        return new FeedRecording
        {
            Source = "synthetic",
            RecordedAt = "2026-09-29",
            Packages =
            [
                // Both ship assembly version 4.0.0.0; the DLL is 4.0.1.4000's file.
                Package("Iesi.Collections", "4.0.0.4000", "net40", Iesi4001 with { FileVersion = "4.0.0.4000" }),
                Package("Iesi.Collections", "4.0.1.4000", "net40", Iesi4001),
                Package("Iesi.Collections", "4.0.4", "net40", Iesi4001 with { FileVersion = "4.0.4.0" }),
                Package("Iesi.Collections", "5.0.0", "net461", Iesi4001 with { Version = "5.0.0.0", FileVersion = "5.0.0.0" }),
                Package("Antlr3.Runtime", "3.5.1", "net40-client", new RecordedAssembly { Name = "Antlr3.Runtime", Version = "3.5.0.2", PublicKey = AntlrKey, FileVersion = "3.5.1.0" }),
                Package("FirebirdSql.Data.FirebirdClient", "2.6.5", "net40", new RecordedAssembly { Name = "FirebirdSql.Data.FirebirdClient", Version = "2.6.5.0", PublicKey = FirebirdKey, FileVersion = "2.6.5.0" }),
                Package("log4net", "1.2.10", "net20", Log4Net1210, listed: false),
                Package("log4net", "2.0.8", "net40-full", new RecordedAssembly { Name = "log4net", Version = "2.0.8.0", PublicKey = Log4NetNewKey }),
                Package("System.Linq.Dynamic", "1.0.0", "net40", new RecordedAssembly { Name = "System.Linq.Dynamic", Version = "1.0.0.0", FileVersion = "1.0.0.0" }),
            ],
        };
    }
}

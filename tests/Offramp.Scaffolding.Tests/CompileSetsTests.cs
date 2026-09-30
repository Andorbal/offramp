using Offramp.Fixtures;
using Offramp.Scaffolding.Csproj;

namespace Offramp.Scaffolding.Tests;

/// <summary>
/// How <c>csproj modernize</c>'s verification tells a different compile set from what the SDK and
/// PackageReference change by themselves (ADR 0061). Cases from SmartStoreNET 4.2.0, where 7 of 11
/// conversions failed only for these.
/// </summary>
public sealed class CompileSetsTests : IDisposable
{
    private const string Framework = "/refs/microsoft.netframework.referenceassemblies.net472/1.0.3/build/.NETFramework/v4.7.2";
    private const string Packages = "/home/user/.nuget/packages";

    private readonly ScratchDirectory _repo = new("compile-sets");

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void Facades_package_assemblies_and_declared_framework_assemblies_are_explained_and_the_rest_differs()
    {
        var before = Set(
            ("SmartStore.Core", "/repo/src/Libraries/SmartStore.Core/bin/Debug/SmartStore.Core.dll"),
            ("System.Runtime", Framework + "/Facades/System.Runtime.dll"),
            ("System.Collections", Framework + "/Facades/System.Collections.dll"),
            ("netstandard", "/sdk/Microsoft/Microsoft.NET.Build.Extensions/net461/lib/netstandard.dll"),
            ("SmartStore.Admin", "/repo/src/Presentation/SmartStore.Web/Administration/bin/SmartStore.Admin.dll"));
        var after = Set(
            ("SmartStore.Core", "/scratch/src/Libraries/SmartStore.Core/bin/Debug/SmartStore.Core.dll"),
            ("Microsoft.Web.Infrastructure", Packages + "/microsoft.web.infrastructure/1.0.0/lib/net40/Microsoft.Web.Infrastructure.dll"),
            ("PresentationCore", Framework + "/PresentationCore.dll"),
            ("System.Drawing", Framework + "/System.Drawing.dll"),
            ("System.Security", Framework + "/System.Security.dll"),
            ("Vendor.Extra", "/repo/lib/Vendor.Extra.dll"));
        var restored = new RestoredPackages
        {
            Assemblies = new Dictionary<string, string>
            {
                ["/microsoft.web.infrastructure/1.0.0/lib/net40/microsoft.web.infrastructure.dll"] = "Microsoft.Web.Infrastructure 1.0.0",
                ["/microsoft.netframework.referenceassemblies.net472/1.0.3/lib/net472/_._"] = "Microsoft.NETFramework.ReferenceAssemblies.net472 1.0.3",
            },
            FrameworkAssemblies = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["PresentationCore"] = ["ImageProcessor 2.9.1"],
            },
        };

        var difference = CompileSets.Compare(before, after, restored: restored);

        Assert.Equal(["Microsoft.Web.Infrastructure"], difference.TransitiveReferencesAdded);
        Assert.Equal(["PresentationCore"], difference.FrameworkReferencesAdded);
        Assert.Equal(["System.Drawing"], difference.ImplicitReferencesAdded);
        Assert.Equal(["netstandard", "System.Collections", "System.Runtime"], difference.FacadesRemoved);
        // System.Security comes from the reference assemblies package's build/ folder: the framework, not a package's assembly.
        Assert.Equal(["System.Security", "Vendor.Extra"], difference.ReferencesAdded);
        Assert.Equal(["SmartStore.Admin"], difference.ReferencesRemoved);
        Assert.False(difference.Identical);
        ReferenceExplanation[] expected =
        [
            new("added", "frameworkAssemblies", "ImageProcessor 2.9.1", ["PresentationCore"]),
            new("added", "package", "Microsoft.Web.Infrastructure 1.0.0", ["Microsoft.Web.Infrastructure"]),
            new("removed", "facades", ".NETFramework/v4.7.2/Facades", ["System.Collections", "System.Runtime"]),
            new("removed", "facades", "Microsoft.NET.Build.Extensions/net461/lib", ["netstandard"]),
        ];
        Assert.Equal(expected, difference.Explanations, new ExplanationComparer());

        // Without the unexplained ones, the compile sets are the same.
        var explainedOnly = CompileSets.Compare(
            before with { References = before.References.Where(r => r != "SmartStore.Admin").ToHashSet(StringComparer.OrdinalIgnoreCase) },
            after with { References = after.References.Where(r => r is not ("Vendor.Extra" or "System.Security")).ToHashSet(StringComparer.OrdinalIgnoreCase) },
            restored: restored);
        Assert.True(explainedOnly.Identical);
    }

    [Fact]
    public void A_framework_assembly_no_restored_package_declares_is_a_difference()
    {
        var before = Set(("System", Framework + "/System.dll"));
        var after = Set(("System", Framework + "/System.dll"), ("System.Security", Framework + "/System.Security.dll"));

        var difference = CompileSets.Compare(before, after);

        Assert.Equal(["System.Security"], difference.ReferencesAdded);
        Assert.Empty(difference.FrameworkReferencesAdded);
        Assert.False(difference.Identical);
    }

    [Fact]
    public void Restored_packages_come_from_the_assets_file_of_the_target_framework()
    {
        var assets = _repo.Write("obj/project.assets.json", """
            {
              "version": 3,
              "targets": {
                ".NETFramework,Version=v4.7.2": {
                  "ImageProcessor/2.9.1": { "type": "package", "frameworkAssemblies": [ "PresentationCore", "System.Web" ], "compile": { "lib/net452/ImageProcessor.dll": {} } },
                  "Microsoft.AspNet.WebApi.Client/5.2.7": { "type": "package", "frameworkAssemblies": [ "System.Net.Http" ], "compile": { "lib/net45/System.Net.Http.Formatting.dll": {} } },
                  "Microsoft.Web.Infrastructure/1.0.0": { "type": "package", "compile": { "lib/net40/Microsoft.Web.Infrastructure.dll": {} } },
                  "SmartStore.Core/1.0.0": { "type": "project", "framework": ".NETFramework,Version=v4.7.2" }
                }
              },
              "libraries": {
                "ImageProcessor/2.9.1": { "sha512": "AA==", "type": "package", "path": "imageprocessor/2.9.1", "files": [] },
                "Microsoft.AspNet.WebApi.Client/5.2.7": { "sha512": "AA==", "type": "package", "path": "microsoft.aspnet.webapi.client/5.2.7", "files": [] },
                "Microsoft.Web.Infrastructure/1.0.0": { "sha512": "AA==", "type": "package", "path": "microsoft.web.infrastructure/1.0.0", "files": [] },
                "SmartStore.Core/1.0.0": { "type": "project", "path": "../SmartStore.Core/SmartStore.Core.csproj", "msbuildProject": "../SmartStore.Core/SmartStore.Core.csproj" }
              },
              "projectFileDependencyGroups": { ".NETFramework,Version=v4.7.2": [] },
              "packageFolders": { "/home/user/.nuget/packages/": {} }
            }
            """);

        var restored = RestoredPackages.Read(assets, "net472");

        Assert.Equal(
            [
                "/imageprocessor/2.9.1/lib/net452/imageprocessor.dll=ImageProcessor 2.9.1",
                "/microsoft.aspnet.webapi.client/5.2.7/lib/net45/system.net.http.formatting.dll=Microsoft.AspNet.WebApi.Client 5.2.7",
                "/microsoft.web.infrastructure/1.0.0/lib/net40/microsoft.web.infrastructure.dll=Microsoft.Web.Infrastructure 1.0.0",
            ],
            restored.Assemblies.Select(f => $"{f.Key}={f.Value}").Order(StringComparer.Ordinal));
        Assert.Equal(["ImageProcessor 2.9.1"], restored.FrameworkAssemblies["PresentationCore"]);
        Assert.Equal(["Microsoft.AspNet.WebApi.Client 5.2.7"], restored.FrameworkAssemblies["System.Net.Http"]);
        Assert.Same(RestoredPackages.None, RestoredPackages.Read(_repo.Combine("obj", "missing.json"), "net472"));
    }

    private static CompileSet Set(params (string Name, string Path)[] references) => new()
    {
        TargetFramework = "net472",
        Sources = new HashSet<string>(StringComparer.Ordinal) { "Class1.cs" },
        References = references.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
        ReferencePaths = references.ToDictionary(r => r.Name, r => r.Path, StringComparer.OrdinalIgnoreCase),
        Resources = new HashSet<string>(StringComparer.Ordinal),
    };

    private sealed class ExplanationComparer : IEqualityComparer<ReferenceExplanation>
    {
        public bool Equals(ReferenceExplanation? x, ReferenceExplanation? y) =>
            x is not null && y is not null && (x.Change, x.Reason, x.Source) == (y.Change, y.Reason, y.Source) && x.References.SequenceEqual(y.References);

        public int GetHashCode(ReferenceExplanation obj) => HashCode.Combine(obj.Change, obj.Reason, obj.Source);
    }
}

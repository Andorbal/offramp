using Offramp.Analysis.DeadCode;
using Offramp.Core.Model;
using Offramp.Fixtures;

namespace Offramp.Analysis.Tests;

public sealed class ShippedProjectsTests : IDisposable
{
    private readonly ScratchDirectory _repo = new("shipped");

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_library_only_tests_and_other_libraries_use_is_shipped()
    {
        // NHibernate's shape: the library is referenced by a domain-model library and by test projects, never by an application.
        var shipped = Read(
            Project("src/Core/Core.csproj", ProjectKind.Library),
            Project("src/Model/Model.csproj", ProjectKind.Library, "src/Core/Core.csproj"),
            Project("src/Core.Test/Core.Test.csproj", ProjectKind.Test, "src/Core/Core.csproj", "src/Model/Model.csproj"),
            Project("src/Used/Used.csproj", ProjectKind.Library),
            Project("src/Helpers/Helpers.csproj", ProjectKind.Library, "src/Used/Used.csproj"),
            Project("src/App/App.csproj", ProjectKind.Console, "src/Helpers/Helpers.csproj"));

        Assert.Equal(new ShippedReason(ShippedRule.NoApplication, "no application in the solution uses it"), shipped["src/Core/Core.csproj"]);
        Assert.Equal(ShippedRule.NoApplication, shipped["src/Model/Model.csproj"]?.Rule);

        // An application uses these, directly or through another library; an application is not a library.
        Assert.Null(shipped["src/Used/Used.csproj"]);
        Assert.Null(shipped["src/Helpers/Helpers.csproj"]);
        Assert.Null(shipped["src/App/App.csproj"]);

        // A test project whose output is a library counts as one: a production library that carries its
        // tests is a test project by kind, and it is what move tests works on.
        Assert.Equal(ShippedRule.NoApplication, shipped["src/Core.Test/Core.Test.csproj"]?.Rule);
    }

    [Fact]
    public void A_nuspec_beside_a_library_or_naming_its_dll_anywhere_ships_it()
    {
        _repo.Write("src/Core/Core.nuspec.template", "<package><metadata><id>${id}</id></metadata></package>");
        _repo.Write("OpenLiveWriter.SDK.nuspec",
            "<?xml version=\"1.0\"?><package xmlns=\"http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd\"><files>"
            + "<file src=\"src\\managed\\bin\\Release\\i386\\Writer\\OpenLiveWriter.Api.dll\" target=\"lib\\net46\" />"
            + "<file src=\"src\\Wild\\bin\\*.dll\" target=\"lib\" /></files></package>");
        _repo.Write("src/Web/Web.nuspec", "<package><files><file src=\"bin\\**\" /></files></package>");
        _repo.Write("src/Broken/Broken.nuspec.template", "<package><metadata><id>@id@</metadata>");
        _repo.Write("src/Api/bin/Release/Leftover.nuspec", "<package />");

        var shipped = Read(
            Project("src/Core/Core.csproj", ProjectKind.Library),
            Project("src/managed/OpenLiveWriter.Api/OpenLiveWriter.Api.csproj", ProjectKind.Library) with { AssemblyName = "OpenLiveWriter.Api" },
            Project("src/Wild/Wild.csproj", ProjectKind.Library),
            Project("src/Web/Web.csproj", ProjectKind.Web),
            Project("src/Broken/Broken.csproj", ProjectKind.Library),
            Project("src/Api/Api.csproj", ProjectKind.Library),
            Project("src/App/App.csproj", ProjectKind.Console,
                "src/Core/Core.csproj", "src/managed/OpenLiveWriter.Api/OpenLiveWriter.Api.csproj", "src/Wild/Wild.csproj", "src/Broken/Broken.csproj", "src/Api/Api.csproj"));

        Assert.Equal(new ShippedReason(ShippedRule.Nuspec, "packed by src/Core/Core.nuspec.template"), shipped["src/Core/Core.csproj"]);
        Assert.Equal(
            new ShippedReason(ShippedRule.Nuspec, "packed as OpenLiveWriter.Api.dll by OpenLiveWriter.SDK.nuspec"),
            shipped["src/managed/OpenLiveWriter.Api/OpenLiveWriter.Api.csproj"]);

        // A template that is not XML still ships the library beside it.
        Assert.Equal(ShippedRule.Nuspec, shipped["src/Broken/Broken.csproj"]?.Rule);

        // A wildcard names no DLL; a nuspec beside an application packs it for deployment; build output is not read.
        Assert.Null(shipped["src/Wild/Wild.csproj"]);
        Assert.Null(shipped["src/Web/Web.csproj"]);
        Assert.Null(shipped["src/Api/Api.csproj"]);
    }

    [Fact]
    public void External_consumers_come_first_then_packable()
    {
        var packable = Project("src/Lib/Lib.csproj", ProjectKind.Library) with
        {
            AssemblyName = "Contoso.Lib",
            Properties = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["IsPackable"] = "true" },
        };
        var app = Project("src/App/App.csproj", ProjectKind.Console, "src/Lib/Lib.csproj");

        var byAssembly = ShippedProjects.Read(_repo.Path, Model(packable, app), ["contoso.lib"]);
        var byPath = ShippedProjects.Read(_repo.Path, Model(packable, app), ["src/App/App.csproj"]);
        var none = ShippedProjects.Read(_repo.Path, Model(packable with { Properties = new SortedDictionary<string, string>(StringComparer.Ordinal) }, app), []);

        Assert.Equal(new ShippedReason(ShippedRule.ExternalConsumer, "listed in deadCode.externalConsumers"), byAssembly.Of(packable));
        Assert.Equal(ShippedRule.ExternalConsumer, byPath.Of(app)?.Rule);
        Assert.Equal(new ShippedReason(ShippedRule.Packable, "packable (IsPackable)"), byPath.Of(packable));
        Assert.Null(none.Of(packable));
    }

    private Dictionary<string, ShippedReason?> Read(params ProjectInfo[] projects)
    {
        var shipped = ShippedProjects.Read(_repo.Path, Model(projects), []);
        return projects.ToDictionary(p => p.Id, shipped.Of);
    }

    private WorkspaceModel Model(params ProjectInfo[] projects) => new()
    {
        CreatedAt = "2026-09-29T00:00:00Z",
        RepositoryRoot = _repo.Path,
        Source = new WorkspaceSource(WorkspaceSourceKind.Build, ".offramp/msbuild.binlog", new string('0', 64)),
        Sdk = new SdkInfo("10.0.100", "linux-x64"),
        Projects = projects,
    };

    private static ProjectInfo Project(string id, ProjectKind kind, params string[] references) => new()
    {
        Id = id,
        Name = Path.GetFileNameWithoutExtension(id),
        Kind = kind,
        OutputType = kind is ProjectKind.Console ? "Exe" : "Library",
        ProjectReferences = references,
    };
}

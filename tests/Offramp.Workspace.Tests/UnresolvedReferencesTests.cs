using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Fixtures;
using Offramp.Workspace.Scanning;
using static Offramp.Workspace.Tests.GraphBuilderTests;

namespace Offramp.Workspace.Tests;

/// <summary>docs/spec/02-workspace-model.md#project-references-outside-the-model.</summary>
public sealed class UnresolvedReferencesTests
{
    [Fact]
    [ProducesDiagnostic("OFR0105")]
    public void References_the_model_cannot_follow_are_recorded_with_a_reason_and_reported()
    {
        var diagnostics = new DiagnosticBag();
        var loading = new List<Diagnostic>();
        var projects = new[]
        {
            Project("src/App/App.csproj", references: ["src/Core/Core.csproj"]),
            Project("src/Core/Core.csproj"),
        };
        var declared = new Dictionary<string, IReadOnlyList<string>>
        {
            ["src/App/App.csproj"] = ["db/Db.sqlproj", "native/Interop.vcxproj", "src/Core/Core.csproj", "vendor/Other/Other.csproj"],
            ["src/Core/Core.csproj"] = ["db/Db.sqlproj"],
        };

        var resolved = UnresolvedReferences.Resolve(projects, declared,
            [new NotLoadedProject("db/Db.sqlproj", "unsupported project type (.sqlproj)")], diagnostics, loading);

        var app = resolved.Single(p => p.Id == "src/App/App.csproj");
        Assert.Equal(
            [
                ("db/Db.sqlproj", "unsupported project type (.sqlproj)"),
                ("native/Interop.vcxproj", "unsupported project type (.vcxproj)"),
                ("vendor/Other/Other.csproj", "not in the build log (outside the solution or slice that was scanned)"),
            ],
            app.UnresolvedReferences.Select(r => (r.Path, r.Reason)));
        Assert.Equal(["src/Core/Core.csproj"], app.ProjectReferences);
        Assert.Equal(["db/Db.sqlproj"], resolved.Single(p => p.Id == "src/Core/Core.csproj").UnresolvedReferences.Select(r => r.Path));

        var reported = diagnostics.ToSortedList();
        Assert.Equal(4, reported.Count);
        Assert.All(reported, d => Assert.Equal("OFR0105", d.Code));
        Assert.Equal(reported, loading);
        var interop = reported.Single(d => d.Project == "src/App/App.csproj" && d.Data["reference"]!.GetValue<string>() == "native/Interop.vcxproj");
        Assert.Equal("Depends on native/Interop.vcxproj, which is not in the model: unsupported project type (.vcxproj).", interop.Message);
    }

    [Fact]
    public void Projects_whose_references_all_resolve_are_returned_as_they_are()
    {
        var diagnostics = new DiagnosticBag();
        var projects = new[] { Project("src/App/App.csproj", references: ["src/Core/Core.csproj"]), Project("src/Core/Core.csproj") };
        var declared = new Dictionary<string, IReadOnlyList<string>> { ["src/App/App.csproj"] = ["src/Core/Core.csproj"] };

        var resolved = UnresolvedReferences.Resolve(projects, declared, [], diagnostics, []);

        Assert.Equal(projects, resolved);
        Assert.Empty(diagnostics.ToSortedList());
    }
}

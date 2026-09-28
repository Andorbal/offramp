using Offramp.Fixtures;
using Offramp.Workspace.Init;

namespace Offramp.Workspace.Tests;

public sealed class InitAnswersTests : IDisposable
{
    private readonly ScratchDirectory _repo = new("init-answers");

    public void Dispose() => _repo.Dispose();

    [Theory]
    [InlineData("Directory.Packages.props", "Directory.Packages.props", "repo")]
    [InlineData("apps/Legacy/Directory.Packages.props", "apps/Legacy/Directory.Packages.props", "solution")]
    [InlineData(" apps\\Legacy\\Legacy.Packages.props ", "apps/Legacy/Legacy.Packages.props", "solution")]
    [InlineData("apps/Legacy/", "apps/Legacy/Directory.Packages.props", "solution")]
    [InlineData("apps/Legacy", "apps/Legacy/Directory.Packages.props", "solution")]
    [InlineData("./eng/Packages.props", "eng/Packages.props", "solution")]
    public void A_central_file_answer_is_a_repository_path(string answer, string file, string scope)
    {
        _repo.Write("apps/Legacy/Legacy.sln", "");

        var parsed = InitAnswers.CpmFile(_repo.Path, answer);

        Assert.Null(parsed.Error);
        Assert.Equal(new CpmLocation(file, scope), parsed.Value);
    }

    [Fact]
    public void An_absolute_central_file_answer_inside_the_repository_becomes_relative()
    {
        var parsed = InitAnswers.CpmFile(_repo.Path, Path.Combine(_repo.Path, "apps", "Legacy", "Directory.Packages.props"));

        Assert.Equal(new CpmLocation("apps/Legacy/Directory.Packages.props", "solution"), parsed.Value);
    }

    [Theory]
    [InlineData("../elsewhere/Directory.Packages.props")]
    [InlineData("apps/Legacy/versions.txt")]
    public void A_central_file_answer_outside_the_repository_or_not_a_props_file_is_refused(string answer)
    {
        var parsed = InitAnswers.CpmFile(_repo.Path, answer);

        Assert.NotNull(parsed.Error);
        Assert.Null(parsed.Value);
    }

    [Fact]
    public void A_pinned_project_must_exist_and_empty_means_every_project()
    {
        _repo.Write("src/Api/Api.csproj", "<Project />");

        Assert.Equal("src/Api/Api.csproj", InitAnswers.Project(_repo.Path, "src\\Api\\Api.csproj").Value);
        Assert.Null(InitAnswers.Project(_repo.Path, "  ").Value);
        Assert.Null(InitAnswers.Project(_repo.Path, "  ").Error);
        Assert.NotNull(InitAnswers.Project(_repo.Path, "src/Api/Missing.csproj").Error);
        Assert.NotNull(InitAnswers.Project(_repo.Path, "src/Api").Error);
    }

    [Theory]
    [InlineData("Newtonsoft.Json", true)]
    [InlineData(" log4net ", true)]
    [InlineData("Newtonsoft Json", false)]
    [InlineData("Newtonsoft.Json 9.0.1", false)]
    [InlineData("", false)]
    public void Package_ids_are_checked(string answer, bool valid)
    {
        Assert.Equal(valid, InitAnswers.PackageId(answer).Error is null);
    }

    [Theory]
    [InlineData("9.0.1", true)]
    [InlineData("2.0.15-beta1", true)]
    [InlineData("latest", false)]
    [InlineData("[9.0,10.0)", false)]
    public void Versions_are_checked_and_kept_as_typed(string answer, bool valid)
    {
        var parsed = InitAnswers.Version(answer);

        Assert.Equal(valid, parsed.Error is null);
        Assert.Equal(valid ? answer : null, parsed.Value);
    }
}

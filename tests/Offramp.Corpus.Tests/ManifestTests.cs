using System.Reflection;
using Offramp.Corpus.Tests.Harness;
using Offramp.Fixtures;

namespace Offramp.Corpus.Tests;

/// <summary>
/// Checks on <c>codebases.json</c> and the corpus test classes. They are not in the Corpus category, so every
/// CI build runs them: a broken entry fails in minutes, not an hour into a corpus run.
/// </summary>
public sealed class ManifestTests
{
    private static readonly List<(Type Type, string Codebase)> TestClasses = [.. typeof(ManifestTests).Assembly.GetTypes()
        .Select(t => (Type: t, Codebase: Trait(t, "Codebase")))
        .Where(t => t.Codebase is not null)
        .Select(t => (t.Type, t.Codebase!))];

    [Fact]
    public void Every_codebase_is_pinned_to_a_commit_and_documented()
    {
        Assert.NotEmpty(CorpusCodebase.All);
        Assert.Equal(CorpusCodebase.All.Count, CorpusCodebase.All.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(CorpusCodebase.All, codebase =>
        {
            Assert.Matches("^[a-z0-9][a-z0-9-]*$", codebase.Name);
            Assert.Matches("^[0-9a-f]{40}$", codebase.Commit);
            Assert.StartsWith("https://", codebase.Repository, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(codebase.Title));
            Assert.False(string.IsNullOrWhiteSpace(codebase.Solution));
            Assert.InRange(codebase.TimeoutMinutes, 30, 350);
            Assert.True(File.Exists(RepositoryFiles.Path(codebase.FieldTest.Split('/'))), $"{codebase.Name}: {codebase.FieldTest} does not exist.");
        });
    }

    [Fact]
    public void Every_codebase_has_exactly_one_corpus_test_class_and_every_class_a_codebase()
    {
        Assert.Equal(
            CorpusCodebase.All.Select(c => c.Name).Order(StringComparer.Ordinal),
            TestClasses.Select(c => c.Codebase).Order(StringComparer.Ordinal));
        Assert.All(TestClasses, c => Assert.Equal("Corpus", Trait(c.Type, "Category")));
    }

    [Fact]
    public void Every_corpus_test_has_a_timeout_that_leaves_the_job_time_to_build_and_report()
    {
        foreach (var (type, name) in TestClasses)
        {
            var limit = (CorpusCodebase.Get(name).TimeoutMinutes - 15) * 60 * 1000;
            var facts = type.GetMethods().Select(m => m.GetCustomAttribute<FactAttribute>()).OfType<FactAttribute>().ToList();
            Assert.NotEmpty(facts);
            Assert.All(facts, f => Assert.InRange(f.Timeout, 1, limit));
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("all", true)]
    [InlineData("dnn", true)]
    [InlineData("nhibernate, dnn", true)]
    [InlineData("nhibernate", false)]
    [InlineData("dnn-old", false)]
    public void OFFRAMP_CORPUS_selects_every_codebase_or_named_ones(string? setting, bool selected)
    {
        Assert.Equal(selected, CorpusRun.IsSelected("dnn", setting));
    }

    private static string? Trait(Type type, string name) =>
        type.GetCustomAttributes<TraitAttribute>().FirstOrDefault(t => t.Name == name)?.Value;
}

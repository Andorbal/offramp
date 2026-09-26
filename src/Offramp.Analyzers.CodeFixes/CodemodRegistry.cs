using System.Collections.Immutable;
using Offramp.Analyzers.CodeFixes.Rules;
using Offramp.Analyzers.Rules;

namespace Offramp.Analyzers.CodeFixes;

/// <summary>A codemod's analyzer and its fixer; a codemod whose rewrite is a package reference has no fixer.</summary>
public sealed class CodemodImplementation
{
    public CodemodImplementation(CodemodAnalyzer analyzer, CodemodFixer? fixer)
    {
        Analyzer = analyzer;
        Fixer = fixer;
    }

    public Codemod Codemod => Analyzer.Codemod;

    public CodemodAnalyzer Analyzer { get; }

    public CodemodFixer? Fixer { get; }
}

/// <summary>Every codemod's implementation, in catalog order: what <c>offramp codemod run</c> drives.</summary>
public static class CodemodRegistry
{
    public static readonly ImmutableArray<CodemodImplementation> All = ImmutableArray.Create(
        new CodemodImplementation(new SqlClientAnalyzer(), new SqlClientFixer()),
        new CodemodImplementation(new ConfigManagerAnalyzer(), new ConfigManagerFixer()),
        new CodemodImplementation(new HttpContextAnalyzer(), new HttpContextFixer()),
        new CodemodImplementation(new WebClientAnalyzer(), new WebClientFixer()),
        new CodemodImplementation(new JavaScriptSerializerAnalyzer(), new JavaScriptSerializerFixer()),
        new CodemodImplementation(new BinaryFormatterCloneAnalyzer(), new BinaryFormatterCloneFixer()),
        new CodemodImplementation(new ThreadAbortAnalyzer(), new ThreadAbortFixer()),
        new CodemodImplementation(new ProcessStartUrlAnalyzer(), new ProcessStartUrlFixer()),
        new CodemodImplementation(new StringComparisonAnalyzer(), new StringComparisonFixer()),
        new CodemodImplementation(new CodePagesAnalyzer(), new CodePagesFixer()),
        new CodemodImplementation(new TimeZoneIdsAnalyzer(), new TimeZoneIdsFixer()),
        new CodemodImplementation(new ServiceControllerAnalyzer(), null),
        new CodemodImplementation(new AssemblyInfoAnalyzer(), new AssemblyInfoFixer()),
        new CodemodImplementation(new ConfigManagerShimAnalyzer(), new ConfigManagerShimFixer()));

    public static CodemodImplementation For(Codemod codemod) => All.First(i => i.Codemod.Id == codemod.Id);
}

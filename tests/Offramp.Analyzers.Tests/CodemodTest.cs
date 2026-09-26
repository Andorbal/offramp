using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace Offramp.Analyzers.Tests;

/// <summary>
/// A before/after pair for one codemod. The testing library applies the fix one site at a
/// time and as fix-all, checks both give the fixed text, and runs the analyzer on the fixed
/// text, which must report nothing fixable: the codemod is idempotent. Then the pair runs
/// through <c>offramp codemod run</c>'s path (<see cref="DirectFix"/>), which must give the
/// same text.
/// </summary>
internal sealed class CodemodTest<TAnalyzer, TFixer> : CSharpCodeFixTest<TAnalyzer, TFixer, DefaultVerifier>
    where TAnalyzer : DiagnosticAnalyzer, new()
    where TFixer : CodeFixProvider, new()
{
    private readonly string _before;
    private readonly string _after;
    private string _globalConfig = "";

    public CodemodTest(string before, string after)
    {
        _before = before;
        _after = after;
        TestCode = before;
        FixedCode = after;
        ReferenceAssemblies = TestReferences.NetFramework;
        // The fixed code uses packages the codemod adds to the project (Microsoft.Data.SqlClient, TimeZoneConverter, ...).
        CompilerDiagnostics = CompilerDiagnostics.None;
        // Skipped sites stay reported after the fix, under the same (fixable) ID.
        FixedState.MarkupHandling = MarkupMode.Allow;
    }

    /// <summary>Global analyzer options, as the build passes them (build_property.*).</summary>
    public CodemodTest<TAnalyzer, TFixer> WithGlobalOptions(string config)
    {
        TestState.AnalyzerConfigFiles.Add(("/.globalconfig", "is_global = true\n" + config));
        _globalConfig = config;
        return this;
    }

    public new async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await base.RunAsync(cancellationToken);
        TestFileMarkupParser.GetSpans(_before, out string before, out ImmutableArray<Microsoft.CodeAnalysis.Text.TextSpan> _);
        TestFileMarkupParser.GetSpans(_after, out string after, out ImmutableArray<Microsoft.CodeAnalysis.Text.TextSpan> _);
        var driver = await DirectFix.ApplyAsync(new TAnalyzer(), (Offramp.Analyzers.CodeFixes.CodemodFixer)(object)new TFixer(), before, OutputKind.DynamicallyLinkedLibrary, cancellationToken, ReferenceAssemblies, _globalConfig);
        Assert.Equal(after.ReplaceLineEndings("\n"), driver.ReplaceLineEndings("\n"));
    }
}

/// <summary>An analyzer-only check: the sites and their skip reasons.</summary>
internal sealed class SitesTest<TAnalyzer> : CSharpCodeFixTest<TAnalyzer, EmptyFixer, DefaultVerifier>
    where TAnalyzer : DiagnosticAnalyzer, new()
{
    public SitesTest(string code)
    {
        TestCode = code;
        FixedCode = code;
        ReferenceAssemblies = TestReferences.NetFramework;
        CompilerDiagnostics = CompilerDiagnostics.None;
    }
}

/// <summary>For analyzer-only tests: fixes nothing.</summary>
public sealed class EmptyFixer : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [];

    public override Task RegisterCodeFixesAsync(CodeFixContext context) => Task.CompletedTask;
}

/// <summary>
/// Applies a fixer the way <c>offramp codemod run</c> does: the analyzer's diagnostics over the
/// whole compilation, one <c>FixDocumentAsync</c>, then the code action cleanup. For codemods
/// whose diagnostics are reported at compilation end, which the testing library does not fix.
/// </summary>
internal static class DirectFix
{
    public static async Task<string> ApplyAsync(DiagnosticAnalyzer analyzer, Offramp.Analyzers.CodeFixes.CodemodFixer fixer, string source, OutputKind kind, CancellationToken cancellationToken,
        ReferenceAssemblies? referenceAssemblies = null, string globalConfig = "")
    {
        var references = await (referenceAssemblies ?? TestReferences.NetFramework).ResolveAsync(LanguageNames.CSharp, cancellationToken);
        using var workspace = new AdhocWorkspace();
        // As the driver does: the codemod's diagnostic is on (opt-in ones too), whatever its default.
        var options = new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(kind)
            .WithSpecificDiagnosticOptions([.. analyzer.SupportedDiagnostics.Select(d => KeyValuePair.Create(d.Id, ReportDiagnostic.Info))]);
        var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "Test", "Test", LanguageNames.CSharp,
            compilationOptions: options, metadataReferences: references));
        var document = workspace.AddDocument(project.Id, "Program.cs", Microsoft.CodeAnalysis.Text.SourceText.From(source));
        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        var values = globalConfig.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .ToImmutableDictionary(pair => pair[0].Trim(), pair => pair[1].Trim());
        var analyzerOptions = new AnalyzerOptions([], new GlobalOptionsProvider(new DictionaryOptions(values)));
        var diagnostics = await compilation!.WithAnalyzers([analyzer], analyzerOptions).GetAnalyzerDiagnosticsAsync(cancellationToken);
        var fixedDocument = await fixer.FixDocumentAsync(document, Offramp.Analyzers.CodeFixes.CodemodFixer.Fixable(diagnostics), cancellationToken);
        fixedDocument = await Offramp.Analyzers.CodeFixes.CodemodFixer.CleanupAsync(fixedDocument, cancellationToken);
        return (await fixedDocument.GetTextAsync(cancellationToken)).ToString();
    }
}

internal sealed class DictionaryOptions(ImmutableDictionary<string, string> values) : AnalyzerConfigOptions
{
    public override bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value) => values.TryGetValue(key, out value);
}

internal sealed class GlobalOptionsProvider(AnalyzerConfigOptions global) : AnalyzerConfigOptionsProvider
{
    public override AnalyzerConfigOptions GlobalOptions => global;

    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => global;

    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => global;
}

internal static class TestReferences
{
    /// <summary>.NET Framework 4.8 with the assemblies the legacy APIs live in.</summary>
    public static readonly ReferenceAssemblies NetFramework = ReferenceAssemblies.NetFramework.Net48.Default
        .AddAssemblies(ImmutableArray.Create("System.Configuration", "System.Data", "System.Web", "System.Web.Extensions", "System.ServiceProcess", "System.Net.Http"));
}

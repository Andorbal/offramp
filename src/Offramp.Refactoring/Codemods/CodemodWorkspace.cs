using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Offramp.Core.Paths;
using RoslynProjectInfo = Microsoft.CodeAnalysis.ProjectInfo;

namespace Offramp.Refactoring.Codemods;

/// <summary>
/// A Roslyn workspace holding one recorded compilation as a project, so code fixes (which
/// work on documents) can run over it: the recorded trees as documents, the recorded
/// references, parse options, and compilation options. The chosen codemods' diagnostics are
/// enabled whatever their default, and the options the analyzers read come from the
/// recording plus what the driver knows about the project.
/// </summary>
internal sealed class CodemodWorkspace : IDisposable
{
    private readonly AdhocWorkspace _workspace = new();

    private CodemodWorkspace(Solution solution, ProjectId project, AnalyzerOptions options)
    {
        Solution = solution;
        Project = project;
        Options = options;
    }

    public Solution Solution { get; set; }

    public ProjectId Project { get; }

    public AnalyzerOptions Options { get; }

    public static CodemodWorkspace Create(string name, string projectPath, Compilation compilation, IEnumerable<string> diagnosticIds, IReadOnlyDictionary<string, string> properties, AnalyzerConfigOptions? recorded)
    {
        var projectId = ProjectId.CreateNewId(name);
        var documents = compilation.SyntaxTrees
            .Select(tree => DocumentInfo.Create(
                DocumentId.CreateNewId(projectId, tree.FilePath),
                Path.GetFileName(tree.FilePath),
                loader: TextLoader.From(TextAndVersion.Create(tree.GetText(), VersionStamp.Default, tree.FilePath)),
                filePath: tree.FilePath))
            .ToList();

        // Per-tree options (.editorconfig, .globalconfig severities) are keyed by the recorded
        // trees; the chosen codemods run at info whatever the repository configures.
        var options = ((CSharpCompilationOptions)compilation.Options)
            .WithSyntaxTreeOptionsProvider(null)
            .WithSpecificDiagnosticOptions(compilation.Options.SpecificDiagnosticOptions.SetItems(diagnosticIds.Select(id => KeyValuePair.Create(id, ReportDiagnostic.Info))));
        var info = RoslynProjectInfo.Create(projectId, VersionStamp.Default, name, compilation.AssemblyName ?? name, LanguageNames.CSharp,
            filePath: projectPath,
            compilationOptions: options,
            parseOptions: compilation.SyntaxTrees.FirstOrDefault()?.Options,
            documents: documents,
            metadataReferences: compilation.References);
        var workspace = new CodemodWorkspace(null!, projectId, new AnalyzerOptions([], new OptionsProvider(new MergedOptions(properties, recorded))));
        workspace.Solution = workspace._workspace.AddProject(info).Solution;
        return workspace;
    }

    /// <summary>
    /// The <c>build_property</c> values the codemods read. <c>UsingMicrosoftNETSdk</c> is the
    /// project's style; <c>GenerateAssemblyInfo</c> is whether the recorded compilation holds the
    /// SDK's generated AssemblyInfo file (the MSBuild WriteCodeFragment output).
    /// </summary>
    public static Dictionary<string, string> Properties(Offramp.Core.Model.ProjectInfo project, Compilation compilation) => new(StringComparer.Ordinal)
    {
        ["build_property.UsingMicrosoftNETSdk"] = project.SdkStyle ? "true" : "false",
        ["build_property.GenerateAssemblyInfo"] = compilation.SyntaxTrees.Any(GeneratedAssemblyInfo) ? "true" : "false",
    };

    /// <summary>The repository-relative path of a tree's file, or null when it is outside the repository.</summary>
    public static string? FileOf(string root, SyntaxTree tree)
    {
        if (!Path.IsPathRooted(tree.FilePath))
        {
            return null;
        }

        var relative = RepoPaths.ToRepositoryRelative(root, Path.GetFullPath(tree.FilePath));
        return relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? null : relative;
    }

    public void Dispose() => _workspace.Dispose();

    private static bool GeneratedAssemblyInfo(SyntaxTree tree) =>
        tree.FilePath.EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase)
        && tree.GetText().ToString(new TextSpan(0, Math.Min(tree.Length, 1024))).Contains("WriteCodeFragment", StringComparison.Ordinal);

    private sealed class MergedOptions(IReadOnlyDictionary<string, string> values, AnalyzerConfigOptions? recorded) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            if (values.TryGetValue(key, out var own))
            {
                value = own;
                return true;
            }

            if (recorded is not null && recorded.TryGetValue(key, out var fromRecording))
            {
                value = fromRecording;
                return true;
            }

            value = "";
            return false;
        }
    }

    private sealed class OptionsProvider(AnalyzerConfigOptions global) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions => global;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => global;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => global;
    }
}

/// <summary>Maps positions in a document's current text back to its original text through the fix stages applied to it.</summary>
internal sealed class PositionMap
{
    private readonly List<IReadOnlyList<TextChange>> _stages = [];

    public void Add(IEnumerable<TextChange> changes) => _stages.Add([.. changes.OrderBy(c => c.Span.Start)]);

    public int ToOriginal(int position)
    {
        for (var stage = _stages.Count - 1; stage >= 0; stage--)
        {
            position = Back(_stages[stage], position);
        }

        return position;
    }

    private static int Back(IReadOnlyList<TextChange> changes, int position)
    {
        var delta = 0;
        foreach (var change in changes)
        {
            var start = change.Span.Start + delta;
            if (position < start)
            {
                return position - delta;
            }

            if (position < start + (change.NewText?.Length ?? 0))
            {
                return change.Span.Start;
            }

            delta += (change.NewText?.Length ?? 0) - change.Span.Length;
        }

        return position - delta;
    }
}

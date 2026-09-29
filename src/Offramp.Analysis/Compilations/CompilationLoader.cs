using System.Collections.Immutable;
using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Workspace.Ingest;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Analysis.Compilations;

/// <summary>
/// Where analyses get a project's compilations: the compiler log as recorded
/// (<see cref="CompilationLoader"/>), or the same with newer text laid over it (the editor).
/// </summary>
public interface ICompilationSource
{
    /// <summary>The compilation of a project for one target framework, or null when there is none.</summary>
    Compilation? LoadForProject(ProjectInfo project, string targetFramework);

    /// <summary>The analyzers and analyzer options recorded for a project's compilation, or null.</summary>
    (ImmutableArray<DiagnosticAnalyzer> Analyzers, AnalyzerOptions Options)? LoadAnalyzers(ProjectInfo project, string targetFramework);
}

public static class CompilationSourceExtensions
{
    /// <summary>The compilation of a project for its first .NET Framework target (or its first target), or null.</summary>
    public static Compilation? LoadPreferred(this ICompilationSource source, ProjectInfo project) =>
        CompilationLoader.PreferredTarget(project) is { } target ? source.LoadForProject(project, target) : null;
}

/// <summary>
/// Rebuilds the Roslyn compilations recorded in the workspace's compiler log
/// (docs/spec/02-workspace-model.md): the exact sources, references, and options
/// the compiler saw, without MSBuild.
/// </summary>
public sealed class CompilationLoader : ICompilationSource, IDisposable
{
    private readonly string _repositoryRoot;
    private readonly Dictionary<string, (CompilerLogReader Reader, IReadOnlyDictionary<(string, string), int> Indexes)> _readers = new(StringComparer.Ordinal);

    public CompilationLoader(string repositoryRoot) => _repositoryRoot = repositoryRoot;

    /// <summary>The compilation for a recorded compiler call, or null when the compiler log is missing or lacks the call.</summary>
    public Compilation? Load(CompilerCallRef call)
    {
        if (Open(call) is not { } found)
        {
            return null;
        }

        var (reader, index) = found;
        var compilation = reader.ReadCompilationData(index).GetCompilationAfterGenerators();
        return WithCoreLibrary(compilation, () => reader.ReadArguments(reader.ReadCompilerCall(index)));
    }

    /// <summary>
    /// A Visual Basic compilation with the core library it was compiled against. Legacy
    /// (non-SDK) Visual Basic projects pass <c>/nostdlib /sdkpath:DIR</c> and no <c>mscorlib</c>
    /// reference: <c>vbc</c> takes <c>mscorlib.dll</c> from the SDK path by itself, and the compiler
    /// log records only the references named on the command line. Without it nothing binds.
    /// Other compilations, and one whose SDK path is gone, are returned as they are.
    /// </summary>
    public static Compilation WithCoreLibrary(Compilation compilation, Func<IEnumerable<string>> arguments)
    {
        if (compilation.Language != LanguageNames.VisualBasic || compilation.GetSpecialType(SpecialType.System_Object).TypeKind != TypeKind.Error)
        {
            return compilation;
        }

        foreach (var argument in arguments())
        {
            var value = argument.StartsWith("/sdkpath:", StringComparison.OrdinalIgnoreCase) || argument.StartsWith("-sdkpath:", StringComparison.OrdinalIgnoreCase)
                ? argument["/sdkpath:".Length..].Trim('"')
                : null;
            var mscorlib = value is null ? null : Path.Combine(value, "mscorlib.dll");
            if (mscorlib is not null && File.Exists(mscorlib))
            {
                return compilation.AddReferences(MetadataReference.CreateFromFile(mscorlib));
            }
        }

        return compilation;
    }

    /// <summary>The compilation of a project for its first .NET Framework target (or its first target), or null.</summary>
    public Compilation? LoadForProject(ProjectInfo project) =>
        PreferredTarget(project) is { } target ? LoadForProject(project, target) : null;

    /// <summary>The compilation of a project for one target framework, or null when it has no compiler call for it.</summary>
    public Compilation? LoadForProject(ProjectInfo project, string targetFramework) =>
        project.CompilerCalls.TryGetValue(targetFramework, out var call) ? Load(call) : null;

    /// <summary>
    /// The analyzers and analyzer options recorded for a project's compilation, or null. The
    /// options answer every syntax tree with the recorded global options (the MSBuild
    /// properties analyzers read): the recorded per-file options are keyed by the recorded
    /// trees, which a rebuilt or trial compilation does not contain.
    /// </summary>
    public (ImmutableArray<DiagnosticAnalyzer> Analyzers, AnalyzerOptions Options)? LoadAnalyzers(ProjectInfo project, string targetFramework)
    {
        if (!project.CompilerCalls.TryGetValue(targetFramework, out var call) || Open(call) is not { } found)
        {
            return null;
        }

        var data = found.Reader.ReadCompilationData(found.Index);
        var global = new GlobalOptionsProvider(data.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions);
        return (data.GetAnalyzers(out _), new AnalyzerOptions(data.AnalyzerOptions.AdditionalFiles, global));
    }

    /// <summary>The recorded global analyzer config options (<c>build_property.*</c>, .globalconfig) of a project's compilation, without loading its analyzers.</summary>
    public AnalyzerConfigOptions? LoadGlobalOptions(ProjectInfo project, string targetFramework) =>
        project.CompilerCalls.TryGetValue(targetFramework, out var call) && Open(call) is { } found
            ? found.Reader.ReadCompilationData(found.Index).AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions
            : null;

    /// <summary>The target framework analyses use by default: the first .NET Framework one, else the first.</summary>
    public static string? PreferredTarget(ProjectInfo project) =>
        project.CompilerCalls.Keys
            .OrderBy(k => k.StartsWith("net4", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(k => k, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>The reader of the call's compiler log and the call's position in it, or null when either is missing.</summary>
    private (CompilerLogReader Reader, int Index)? Open(CompilerCallRef call)
    {
        var path = RepoPaths.ToAbsolute(_repositoryRoot, call.Complog);
        if (!_readers.TryGetValue(path, out var opened))
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var reader = CompilerLogReader.Create(path, null, null);
            opened = (reader, CompilerLogIngest.CallIndexes(reader, _repositoryRoot));
            _readers[path] = opened;
        }

        return opened.Indexes.TryGetValue((call.Project, call.TargetFramework ?? ""), out var index) ? (opened.Reader, index) : null;
    }

    public void Dispose()
    {
        foreach (var (reader, _) in _readers.Values)
        {
            reader.Dispose();
        }

        _readers.Clear();
    }

    private sealed class GlobalOptionsProvider(AnalyzerConfigOptions global) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions => global;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => global;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => global;
    }
}

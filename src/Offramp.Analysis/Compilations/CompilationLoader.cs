using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Analysis.Compilations;

/// <summary>
/// Rebuilds the Roslyn compilations recorded in the workspace's compiler log
/// (docs/spec/02-workspace-model.md): the exact sources, references, and options
/// the compiler saw, without MSBuild.
/// </summary>
public sealed class CompilationLoader : IDisposable
{
    private readonly string _repositoryRoot;
    private readonly Dictionary<string, CompilerLogReader> _readers = new(StringComparer.Ordinal);

    public CompilationLoader(string repositoryRoot) => _repositoryRoot = repositoryRoot;

    /// <summary>The compilation for a recorded compiler call, or null when the compiler log is missing.</summary>
    public Compilation? Load(CompilerCallRef call)
    {
        var path = RepoPaths.ToAbsolute(_repositoryRoot, call.Complog);
        if (!File.Exists(path))
        {
            return null;
        }

        if (!_readers.TryGetValue(path, out var reader))
        {
            reader = CompilerLogReader.Create(path, null, null);
            _readers[path] = reader;
        }

        return reader.ReadCompilationData(call.Index).GetCompilationAfterGenerators();
    }

    /// <summary>The compilation of a project for its first .NET Framework target (or its first target), or null.</summary>
    public Compilation? LoadForProject(ProjectInfo project) =>
        PreferredTarget(project) is { } target ? LoadForProject(project, target) : null;

    /// <summary>The compilation of a project for one target framework, or null when it has no compiler call for it.</summary>
    public Compilation? LoadForProject(ProjectInfo project, string targetFramework) =>
        project.CompilerCalls.TryGetValue(targetFramework, out var call) ? Load(call) : null;

    /// <summary>The target framework analyses use by default: the first .NET Framework one, else the first.</summary>
    public static string? PreferredTarget(ProjectInfo project) =>
        project.CompilerCalls.Keys
            .OrderBy(k => k.StartsWith("net4", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(k => k, StringComparer.Ordinal)
            .FirstOrDefault();

    public void Dispose()
    {
        foreach (var reader in _readers.Values)
        {
            reader.Dispose();
        }

        _readers.Clear();
    }
}

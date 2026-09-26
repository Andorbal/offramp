using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;

namespace Offramp.Workspace.Ingest;

/// <summary>A compiler invocation in a compiler log, with capture paths still absolute.</summary>
public sealed record CompilerCallInfo(int Index, string ProjectFile, string? TargetFramework, bool IsCSharp)
{
    /// <summary>Preprocessor symbols the compiler saw (including the SDK's implicit ones such as NETFRAMEWORK).</summary>
    public IReadOnlyList<string> Defines { get; init; } = [];
}

/// <summary>The outcome of converting a binary log to a compiler log.</summary>
public sealed record ConversionReport(bool Succeeded, IReadOnlyList<string> Problems);

/// <summary>What one compiler call says about its project (used when only a compiler log is available).</summary>
public sealed record CompiledProject
{
    public required CompilerCallInfo Call { get; init; }

    public required string AssemblyName { get; init; }

    public required string OutputType { get; init; }

    public string? LangVersion { get; init; }

    public string? Nullable { get; init; }

    public required IReadOnlyList<string> Defines { get; init; }

    /// <summary>Source files (not generated ones) as captured.</summary>
    public required IReadOnlyList<string> Sources { get; init; }

    /// <summary>Indexes of other calls this call references (project references).</summary>
    public required IReadOnlyList<int> ReferencedCalls { get; init; }

    /// <summary>Referenced files that are not other calls' outputs, as captured.</summary>
    public required IReadOnlyList<string> ReferencedFiles { get; init; }

    /// <summary><c>build_property.*</c> values from the generated MSBuild editorconfig.</summary>
    public required IReadOnlyDictionary<string, string> BuildProperties { get; init; }
}

/// <summary>Converts binary logs to compiler logs and reads compiler calls with Basic.CompilerLog.</summary>
public static class CompilerLogIngest
{
    private const string GeneratedEditorConfigSuffix = ".GeneratedMSBuildEditorConfig.editorconfig";

    public static ConversionReport Convert(string binlogPath, string complogPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(complogPath)!);
        var result = CompilerLogUtil.TryConvertBinaryLog(binlogPath, complogPath, null);
        return new ConversionReport(result.Succeeded, [.. result.Diagnostics.Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>The regular compiler calls (no satellite or XAML temporary compiles), in log order.</summary>
    public static IReadOnlyList<CompilerCallInfo> ReadCalls(string complogPath)
    {
        using var reader = CompilerLogReader.Create(complogPath, null, null);
        return Calls(reader);
    }

    public static IReadOnlyList<CompiledProject> ReadProjects(string complogPath)
    {
        using var reader = CompilerLogReader.Create(complogPath, null, null);
        var calls = Calls(reader);
        var result = new List<CompiledProject>();
        foreach (var info in calls)
        {
            var call = reader.ReadCompilerCall(info.Index);
            var data = reader.ReadCompilerCallData(call);
            var sources = reader.ReadAllSourceTextData(call);
            var projectDirectory = Path.GetDirectoryName(info.ProjectFile.Replace('\\', '/'))!.Replace('\\', '/');
            var generatedRoot = projectDirectory + "/obj/";

            var referencedCalls = new List<int>();
            var referencedFiles = new List<string>();
            foreach (var reference in reader.ReadAllReferenceData(call))
            {
                if (reader.TryGetCompilerCallIndex(reference.Mvid, out var target) && target != info.Index)
                {
                    referencedCalls.Add(target);
                }
                else
                {
                    referencedFiles.Add(reference.FilePath);
                }
            }

            result.Add(new CompiledProject
            {
                Call = info,
                AssemblyName = Path.GetFileNameWithoutExtension(data.AssemblyFileName),
                OutputType = data.CompilationOptions.OutputKind switch
                {
                    OutputKind.ConsoleApplication => "Exe",
                    OutputKind.WindowsApplication => "WinExe",
                    OutputKind.WindowsRuntimeApplication => "AppContainerExe",
                    OutputKind.NetModule => "Module",
                    _ => "Library",
                },
                LangVersion = data.ParseOptions switch
                {
                    CSharpParseOptions cs => cs.SpecifiedLanguageVersion.ToDisplayString(),
                    VisualBasicParseOptions vb => vb.SpecifiedLanguageVersion.ToString(),
                    _ => null,
                },
                Nullable = data.CompilationOptions is CSharpCompilationOptions csOptions
                    ? csOptions.NullableContextOptions.ToString().ToLowerInvariant()
                    : null,
                Defines = info.Defines,
                Sources = [.. sources
                    .Where(s => s.SourceTextKind == SourceTextKind.SourceCode)
                    .Select(s => s.FilePath)
                    .Where(p => !p.Replace('\\', '/').StartsWith(generatedRoot, StringComparison.OrdinalIgnoreCase))],
                ReferencedCalls = [.. referencedCalls.Distinct().Order()],
                ReferencedFiles = [.. referencedFiles.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                BuildProperties = ReadBuildProperties(reader, sources),
            });
        }

        return result;
    }

    private static List<CompilerCallInfo> Calls(CompilerLogReader reader)
    {
        var result = new List<CompilerCallInfo>();
        var all = reader.ReadAllCompilerCalls(null);
        for (var i = 0; i < all.Count; i++)
        {
            var call = all[i];
            if (call.Kind != CompilerCallKind.Regular)
            {
                continue;
            }

            var data = reader.ReadCompilerCallData(call);
            result.Add(new CompilerCallInfo(i, call.ProjectFilePath, Tfm.Normalize(call.TargetFramework), call.IsCSharp)
            {
                Defines = [.. data.ParseOptions.PreprocessorSymbolNames],
            });
        }

        return result;
    }

    private static Dictionary<string, string> ReadBuildProperties(CompilerLogReader reader, IEnumerable<SourceTextData> sources)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var config = sources.FirstOrDefault(s => s.SourceTextKind == SourceTextKind.AnalyzerConfig
            && s.FilePath.EndsWith(GeneratedEditorConfigSuffix, StringComparison.OrdinalIgnoreCase));
        if (config is null)
        {
            return properties;
        }

        foreach (var line in reader.ReadSourceText(config).Lines)
        {
            var text = line.ToString().Trim();
            const string prefix = "build_property.";
            if (!text.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var equals = text.IndexOf('=', StringComparison.Ordinal);
            if (equals > prefix.Length)
            {
                properties[text[prefix.Length..equals].Trim()] = text[(equals + 1)..].Trim();
            }
        }

        return properties;
    }
}

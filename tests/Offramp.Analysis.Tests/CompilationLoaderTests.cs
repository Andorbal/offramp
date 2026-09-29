using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Offramp.Analysis.Compilations;
using Offramp.Fixtures;

namespace Offramp.Analysis.Tests;

public sealed class CompilationLoaderTests
{
    [Fact]
    public async Task A_legacy_visual_basic_compilation_gets_mscorlib_from_its_sdk_path()
    {
        // Building a net48 fixture puts the reference assemblies in NuGet's global packages folder.
        await ScannedFixtures.GetAsync("webforms");
        var sdk = Path.Combine(GlobalPackages(), "microsoft.netframework.referenceassemblies.net48", "1.0.3", "build", ".NETFramework", "v4.8");
        Assert.True(File.Exists(Path.Combine(sdk, "mscorlib.dll")), sdk);

        // What the compiler log rebuilds from `vbc /nostdlib /sdkpath:DIR`: no core library.
        var legacy = VisualBasicCompilation.Create(
            "Legacy",
            [VisualBasicSyntaxTree.ParseText("Public Class Greeter\n    Public Function Greet() As String\n        Return \"hi\"\n    End Function\nEnd Class\n")],
            options: new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Equal(TypeKind.Error, legacy.GetSpecialType(SpecialType.System_Object).TypeKind);

        var repaired = CompilationLoader.WithCoreLibrary(legacy, () => ["/nostdlib", $"/sdkpath:\"{sdk}\""]);
        Assert.NotEqual(TypeKind.Error, repaired.GetSpecialType(SpecialType.System_Object).TypeKind);
        Assert.DoesNotContain(repaired.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);

        // Nothing to take it from: left as it is.
        Assert.Same(legacy, CompilationLoader.WithCoreLibrary(legacy, () => ["/nostdlib", "/sdkpath:" + Path.Combine(sdk, "missing")]));
    }

    private static string GlobalPackages() =>
        Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
}

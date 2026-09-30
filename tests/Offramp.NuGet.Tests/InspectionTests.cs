using NuGet.Frameworks;
using Offramp.Fixtures.Feeds;
using Offramp.NuGet.Inspection;

namespace Offramp.NuGet.Tests;

public sealed class InspectionTests
{
    private static PackageInspection Inspect(string id, string version) =>
        PackageInspector.Inspect(FeedMaterializer.Nupkg(VersionsFeed.Load().Packages.Single(p => p.Id == id && p.Version == version)));

    [Theory]
    [InlineData("Newtonsoft.Json", "13.0.3", "net10.0", true)]
    [InlineData("Newtonsoft.Json", "8.0.3", "net10.0", false)]
    [InlineData("Newtonsoft.Json", "9.0.1", "net10.0", true)]
    [InlineData("Newtonsoft.Json", "3.5.8", "net48", true)]
    [InlineData("EntityFramework", "6.2.0", "net10.0", false)]
    [InlineData("EntityFramework", "6.4.4", "net10.0", true)]
    [InlineData("EntityFramework", "6.4.4", "net8.0", true)]
    [InlineData("Microsoft.AspNet.WebApi.Core", "5.2.9", "net10.0", false)]
    [InlineData("Microsoft.AspNet.WebApi.Core", "5.2.9", "net48", true)]
    [InlineData("Contoso.Legacy.Reports", "1.0.0", "net10.0", false)]
    public void Support_follows_nuget_compatibility(string id, string version, string target, bool expected) =>
        Assert.Equal(expected, TargetSupport.Supports(Inspect(id, version), NuGetFramework.Parse(target)));

    [Fact]
    public void Frameworks_come_from_every_asset_folder()
    {
        var inspection = Inspect("Microsoft.Extensions.Logging.Abstractions", "8.0.0");

        Assert.Contains("net8.0", inspection.AssetFrameworks);
        Assert.Contains("netstandard2.0", inspection.AssetFrameworks);
        Assert.Contains("net462", inspection.AssetFrameworks);
    }

    [Fact]
    public void Windows_only_evidence_is_for_the_assets_nuget_would_pick()
    {
        var drawing = Inspect("System.Drawing.Common", "8.0.0");

        Assert.Contains("windows6.1", TargetSupport.WindowsOnly(drawing, NuGetFramework.Parse("net10.0")), StringComparison.Ordinal);
        // .NET Standard consumers get the netstandard2.0 build, which carries no attribute.
        Assert.Null(TargetSupport.WindowsOnly(drawing, NuGetFramework.Parse("netstandard2.0")));
        Assert.Null(TargetSupport.WindowsOnly(Inspect("Newtonsoft.Json", "13.0.3"), NuGetFramework.Parse("net10.0")));
    }

    [Fact]
    public void A_reference_to_the_registry_is_not_evidence_of_windows_only_code()
    {
        // NUnit's net6.0 build references Microsoft.Win32.Registry, part of .NET on every OS, and runs on Linux.
        static PackageInspection Package(string reference) => PackageInspector.Inspect(FeedMaterializer.Nupkg(new RecordedPackage
        {
            Id = "Contoso.Runner",
            Version = "1.0.0",
            Files =
            [
                new RecordedFile
                {
                    Path = "lib/net6.0/Contoso.Runner.dll",
                    Assembly = new RecordedAssembly { Name = "Contoso.Runner", Version = "1.0.0.0", References = [new RecordedAssemblyReference(reference, "8.0.0.0", "b03f5f7f11d50a3a")] },
                },
            ],
        }));

        Assert.Null(TargetSupport.WindowsOnly(Package("Microsoft.Win32.Registry"), NuGetFramework.Parse("net10.0")));
        Assert.Contains("references System.Windows.Forms", TargetSupport.WindowsOnly(Package("System.Windows.Forms"), NuGetFramework.Parse("net10.0")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("lib/net45/A.dll", "net45")]
    [InlineData("lib/A.dll", "net11")]
    [InlineData("ref/netstandard2.0/A.dll", "netstandard2.0")]
    [InlineData("runtimes/win/lib/net8.0/A.dll", "net8.0")]
    [InlineData("contentFiles/cs/net6.0/X.cs", "net6.0")]
    [InlineData("buildTransitive/net462/A.targets", "net462")]
    [InlineData("build/A.targets", null)]
    [InlineData("tools/A.ps1", null)]
    [InlineData("lib/nonsense-framework/A.dll", null)]
    public void Asset_frameworks_are_read_from_folders(string path, string? expected) =>
        Assert.Equal(expected, PackageInspector.AssetFramework(path)?.GetShortFolderName());

    [Fact]
    public void A_package_without_assets_or_groups_supports_everything() =>
        Assert.True(TargetSupport.Supports(
            new PackageInspection { Id = "Tools", Version = "1.0.0", AssetFrameworks = [], DependencyFrameworks = [], Assemblies = [] },
            NuGetFramework.Parse("net10.0")));

    [Fact]
    public void A_package_whose_only_code_is_native_windows_code_is_windows_only()
    {
        static PackageInspection Package(params string[] natives) => PackageInspector.Inspect(FeedMaterializer.Nupkg(new RecordedPackage
        {
            Id = "Contoso.Native",
            Version = "1.0.0",
            Files = [.. natives.Select(n => new RecordedFile { Path = n }), new RecordedFile { Path = "build/Contoso.Native.props", Content = "<Project />" }],
        }));

        var windows = Package("runtimes/win-x86/native/contoso.dll", "runtimes/win-x64/native/contoso.dll");
        // JavaScriptEngineSwitcher.V8.Native.win-x64: a mixed-mode assembly under runtimes/win-x64/lib as well.
        var mixed = PackageInspector.Inspect(FeedMaterializer.Nupkg(new RecordedPackage
        {
            Id = "Contoso.Engine.win-x64",
            Version = "1.0.0",
            Files =
            [
                new RecordedFile { Path = "runtimes/win-x64/native/engine-x64.dll" },
                new RecordedFile { Path = "runtimes/win-x64/lib/netcoreapp3.1/Engine-64.dll", Assembly = new RecordedAssembly { Name = "Engine-64", Version = "1.0.0.0" } },
            ],
        }));
        var everywhere = Package("runtimes/win-x64/native/contoso.dll", "runtimes/linux-x64/native/libcontoso.so");

        Assert.Equal(["runtimes/win-x64/native/contoso.dll", "runtimes/win-x86/native/contoso.dll"], windows.NativeAssets);
        Assert.False(TargetSupport.HasAssemblies(windows));
        Assert.Equal("runtimes/win-x64/native/contoso.dll: native code for Windows only", TargetSupport.WindowsOnly(windows, NuGetFramework.Parse("net10.0")));
        Assert.Null(TargetSupport.WindowsOnly(everywhere, NuGetFramework.Parse("net10.0")));
        Assert.Equal("runtimes/win-x64/native/engine-x64.dll: native code for Windows only", TargetSupport.WindowsOnly(mixed, NuGetFramework.Parse("net10.0")));
        Assert.True(TargetSupport.HasAssemblies(Inspect("Newtonsoft.Json", "13.0.3")));
    }

    /// <summary>
    /// Open Live Writer field test (P2): DeltaCompressionDotNet's netstandard2.0 DLL calls msdelta.dll
    /// and PlatformSpellCheck wraps a Windows COM API; both were "not Windows-only". A call into
    /// kernel32, which portable code guards, is not evidence.
    /// </summary>
    [Theory]
    [InlineData("[System.Runtime.InteropServices.DllImport(\"msdelta.dll\")] static extern int ApplyDeltaB(int a);", "calls msdelta.dll (P/Invoke)")]
    [InlineData("[System.Runtime.InteropServices.DllImport(\"User32\")] static extern int GetDpiForWindow(System.IntPtr w);", "calls user32.dll (P/Invoke)")]
    [InlineData("[System.Runtime.InteropServices.DllImport(\"kernel32.dll\")] static extern int GetCurrentThreadId();", null)]
    [InlineData("[System.Runtime.InteropServices.DllImport(\"ole32.dll\")] static extern void CoTaskMemFree(System.IntPtr p);", null)]
    [InlineData("static int Portable() => 1;", null)]
    public void Native_calls_into_windows_libraries_are_windows_only(string member, string? expected) =>
        Assert.Equal(expected, PackageInspector.WindowsEvidence(Compile($"public static class Native {{ {member} }}")));

    [Fact]
    public void A_com_class_is_windows_only_and_a_com_interface_alone_is_not()
    {
        const string Interface = """
            namespace Contoso.Spelling
            {
                [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("b7c82d61-fbe8-4b47-9b27-6c0d2e0de0a3"),
                 System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
                public interface ISpellCheckerFactory { }
            }
            """;
        const string Class = """
            namespace Contoso.Spelling
            {
                [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("7ab36653-1796-484b-bdfa-e74f1db7c1dc")]
                internal class SpellCheckerFactoryClass { }
            }
            """;

        Assert.Null(PackageInspector.WindowsEvidence(Compile(Interface)));
        Assert.Equal("creates the COM class Contoso.Spelling.SpellCheckerFactoryClass ([ComImport])", PackageInspector.WindowsEvidence(Compile(Interface + Class)));
    }

    private static byte[] Compile(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) is "System.Private.CoreLib.dll" or "System.Runtime.dll" or "System.Runtime.InteropServices.dll")
            .Select(p => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(p));
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("Contoso.Native",
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source)], references,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        return stream.ToArray();
    }

    [Fact]
    public void Assembly_facts_identify_a_build()
    {
        var bytes = StubAssembly.Build(new RecordedAssembly
        {
            Name = "Iesi.Collections", Version = "4.0.0.0", FileVersion = "4.0.1.4000", InformationalVersion = "4.0.1.4000-GA",
            References = [new RecordedAssemblyReference("mscorlib", "4.0.0.0", AssemblyFacts.FrameworkCorlibToken)],
        });
        var legacy = StubAssembly.Build(new RecordedAssembly
        {
            Name = "log4net", Version = "1.2.10.0", References = [new RecordedAssemblyReference("mscorlib", "2.0.0.0", AssemblyFacts.FrameworkCorlibToken)],
        });

        var facts = AssemblyFacts.Read(bytes)!;

        Assert.Equal(("Iesi.Collections", "4.0.0.0", "4.0.1.4000", "4.0.1.4000-GA"), (facts.Name, facts.Version, facts.FileVersion, facts.InformationalVersion));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), facts.Sha256);
        Assert.Equal((null, "4.0.0.0", ".NETFramework,Version=v4.0"), (facts.TargetFramework, facts.FrameworkCorlib, facts.InferredFramework));
        Assert.Equal(".NETFramework,Version=v2.0", AssemblyFacts.Read(legacy)!.InferredFramework);
        Assert.Null(AssemblyFacts.Read(StubAssembly.Build(new RecordedAssembly { Name = "Portable", Version = "1.0.0.0" }))!.InferredFramework);
        Assert.Null(AssemblyFacts.Read([1, 2, 3]));
    }

    [Fact]
    public void A_meta_package_uses_its_dependency_groups() =>
        Assert.False(TargetSupport.Supports(
            new PackageInspection { Id = "Meta", Version = "1.0.0", AssetFrameworks = [], DependencyFrameworks = ["net45"], Assemblies = [] },
            NuGetFramework.Parse("net10.0")));
}

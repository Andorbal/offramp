using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Offramp.Analysis.Audits;
using Offramp.Analysis.Seams;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Fixtures;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Analysis.Tests;

/// <summary>What taints a type in <c>seams</c>, on small in-memory projects (ADRs 0053, 0054).</summary>
public sealed class SeamsTaintTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "offramp-seams-taint");

    private static readonly MetadataReference Corlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);

    /// <summary>A stand-in for an API that cannot port.</summary>
    private static readonly MetadataReference Legacy = Assembly("Legacy", """
        namespace Legacy.Registry
        {
            public static class Hive { public static string Read(string key) { return key; } }
        }
        """);

    /// <summary>NHibernate 4.1 P1 #9: calls between types are places to cut, not a cycle that moves together.</summary>
    [Fact]
    public void A_cycle_of_calls_does_not_spread_the_taint()
    {
        var (result, _) = Analyze("list", Library, [Legacy], ["Legacy.Registry"],
            ("Settings.cs", """
                namespace Lib
                {
                    public class Settings
                    {
                        public static string Get(string key) { return Legacy.Registry.Hive.Read(key); }
                        public static void Reload() { Session.Reset(); }
                    }
                }
                """),
            ("Session.cs", """
                namespace Lib
                {
                    public class Session
                    {
                        public static void Reset() { }
                        public string Name() { return Settings.Get("name"); }
                    }
                }
                """),
            ("App.cs", """
                namespace Lib
                {
                    public class App
                    {
                        public string Run() { return new Session().Name(); }
                    }
                }
                """));

        Assert.Equal(["Lib.Settings"], result.Tainted.Select(t => t.Type));
        var seam = Assert.Single(result.Seams);
        Assert.Equal("Lib.Settings", seam.BoundaryType);
        Assert.Equal(["Lib.Session"], seam.Callers);
    }

    /// <summary>NHibernate 4.1 P1 #9: a structural cycle moves together, and its reason names it once.</summary>
    [Fact]
    public void A_structural_cycle_moves_together_and_is_named_by_its_partition()
    {
        var (result, _) = Analyze("list", Library, [Legacy], ["Legacy.Registry"],
            ("Dialect.cs", """
                namespace Lib
                {
                    public class Dialect
                    {
                        internal Registry Registry;
                        public string Quote() { return Legacy.Registry.Hive.Read("q"); }
                    }
                }
                """),
            ("Registry.cs", """
                namespace Lib
                {
                    public class Registry
                    {
                        internal Dialect Default;
                    }
                }
                """),
            ("Printer.cs", """
                namespace Lib
                {
                    public class Printer
                    {
                        public string Use(Dialect dialect) { return dialect.Quote(); }
                    }
                }
                """));

        Assert.Equal(["Lib.Dialect", "Lib.Printer", "Lib.Registry"], result.Tainted.Select(t => t.Type));
        var partition = Assert.Single(result.Partitions, p => p.Types.Contains("Lib.Registry"));
        Assert.Equal(["Lib.Dialect", "Lib.Registry"], partition.Types);
        Assert.Equal([$"in a structural cycle with a tainted type (partition {partition.Id})"], result.Tainted.Single(t => t.Type == "Lib.Registry").Reason);
        Assert.Equal(["exposes Lib.Dialect in Use"], result.Tainted.Single(t => t.Type == "Lib.Printer").Reason);
    }

    /// <summary>NHibernate 4.1 P1 #9: an API a package supplies on the target is not unportable.</summary>
    [Fact]
    [ProducesDiagnostic("OFR4032")]
    public void An_api_a_package_supplies_does_not_taint()
    {
        (string, string)[] files =
        [
            ("Config.cs", "namespace Lib\n{\n    public class Config\n    {\n        public string Get() { return \"\"; }\n    }\n}\n"),
            ("Directory.cs", "namespace Lib\n{\n    public class Directory\n    {\n        public string Find() { return \"\"; }\n    }\n}\n"),
            ("Context.cs", "namespace Lib\n{\n    public class Context\n    {\n        public object Current() { return null; }\n    }\n}\n"),
        ];
        AuditFinding[] findings =
        [
            Finding("OFR3001", "Config.cs", "System.Configuration.ConfigurationManager.AppSettings", ("mapping", "package"), ("package", "System.Configuration.ConfigurationManager")),
            Finding("OFR3001", "Directory.cs", "System.DirectoryServices.DirectorySearcher", ("mapping", "package"), ("package", "System.DirectoryServices"), ("windowsOnly", "true")),
            Finding("OFR3013", "Context.cs", "System.Runtime.Remoting.Messaging.CallContext.GetData"),
        ];

        var (portable, bag) = Analyze("audit", Library, [], [], files, findings);
        var (desktop, _) = Analyze("audit", Library with { Kind = ProjectKind.Winforms }, [], [], files, findings);

        Assert.Equal(["Lib.Context", "Lib.Directory"], portable.Tainted.Select(t => t.Type));
        Assert.Equal(["Lib.Context"], desktop.Tainted.Select(t => t.Type));
        var supplied = Assert.Single(bag.ToSortedList(), d => d.Code == "OFR4032");
        Assert.Equal("System.Configuration.ConfigurationManager", supplied.Data!["package"]!.GetValue<string>());
        Assert.Contains("Lib.Config", supplied.Message, StringComparison.Ordinal);
    }

    /// <summary>NHibernate 4.1 P1 #9: moving most of a project is a split, not a seam.</summary>
    [Fact]
    [ProducesDiagnostic("OFR4031")]
    public void An_extraction_of_more_than_a_quarter_of_the_project_is_not_proposed()
    {
        var files = new List<(string, string)>
        {
            ("Node.cs", "namespace Lib { public class Node { public string Key() { return Legacy.Registry.Hive.Read(\"k\"); } } }"),
            ("Entry.cs", "namespace Lib { public class Entry { public string Run() { return new Node1().Key(); } } }"),
        };
        files.AddRange(Enumerable.Range(1, 12).Select(i => ($"Node{i}.cs", $"namespace Lib {{ public class Node{i} : Node {{ }} }}")));
        files.AddRange(Enumerable.Range(1, 12).Select(i => ($"Plain{i}.cs", $"namespace Lib {{ public class Plain{i} {{ }} }}")));

        var (result, bag) = Analyze("list", Library, [Legacy], ["Legacy.Registry"], [.. files]);

        Assert.Equal(13, result.Tainted.Count);
        Assert.Null(result.Extraction);
        var oversized = Assert.Single(bag.ToSortedList(), d => d.Code == "OFR4031");
        Assert.Equal(["Lib.Node"], oversized.Data!["directlyTainted"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains("13 of its 26 types (50%)", oversized.Message, StringComparison.Ordinal);
    }

    /// <summary>Open Live Writer 0.6.3: COM interop and P/Invoke into Windows libraries are unportable off Windows.</summary>
    [Fact]
    public void Com_interop_and_windows_pinvoke_taint_a_cross_platform_target_only()
    {
        var interop = Assembly("Interop", """
            using System.Runtime.InteropServices;
            namespace Interop
            {
                [ComImport, Guid("332C4425-26CB-11D0-B483-00C04FD90119")]
                public interface IHTMLDocument2 { string title { get; } }
                public static class User32 { [DllImport("user32.dll")] public static extern int GetDoubleClickTime(); }
                public static class Sqlite { [DllImport("sqlite3")] public static extern int sqlite3_libversion_number(); }
            }
            """);
        (string, string)[] files =
        [
            ("Editor.cs", "namespace Lib { public class Editor { internal string Title(Interop.IHTMLDocument2 document) { return document.title; } } }"),
            ("Mouse.cs", "namespace Lib { public class Mouse { public int DoubleClick() { return Interop.User32.GetDoubleClickTime(); } } }"),
            ("Store.cs", "namespace Lib { public class Store { public int Version() { return Interop.Sqlite.sqlite3_libversion_number(); } } }"),
            ("IOleThing.cs", "namespace Lib { [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid(\"00000112-0000-0000-C000-000000000046\")] internal interface IOleThing { } }"),
            ("Native.cs", "namespace Lib { internal static class Native { [System.Runtime.InteropServices.DllImport(\"kernel32\")] internal static extern uint GetTickCount(); } }"),
            ("Clock.cs", "namespace Lib { public class Clock { public uint Now() { return Native.GetTickCount(); } } }"),
        ];

        var (portable, _) = Analyze("audit", Library, [interop], [], files);
        var (desktop, _) = Analyze("audit", Library with { Kind = ProjectKind.Winforms }, [interop], [], files);
        var (listed, _) = Analyze("list", Library, [interop], [], files);

        Assert.Equal(
            [
                ("Lib.Editor", "COM interop: Interop.IHTMLDocument2"),
                ("Lib.IOleThing", "COM interop ([ComImport])"),
                ("Lib.Mouse", "P/Invoke into user32.dll (Interop.User32.GetDoubleClickTime)"),
                ("Lib.Native", "P/Invoke into kernel32 (GetTickCount)"),
            ],
            portable.Tainted.Select(t => (t.Type, Assert.Single(t.Reason))));
        Assert.Empty(desktop.Tainted);
        Assert.Empty(listed.Tainted);
    }

    private static readonly ProjectInfo Library = new() { Id = "src/Lib/Lib.csproj", Name = "Lib", Kind = ProjectKind.Library };

    private static AuditFinding Finding(string rule, string file, string symbol, params (string Key, string Value)[] details) => new()
    {
        Rule = rule,
        Severity = Severity.Error,
        Project = Library.Id,
        File = "src/Lib/" + file,
        Line = 3,
        Column = 5,
        Symbol = symbol,
        Category = "api",
        Message = symbol,
        Recommendation = "",
        Details = new SortedDictionary<string, string>(details.ToDictionary(d => d.Key, d => d.Value), StringComparer.Ordinal),
    };

    private static (SeamsResult Result, DiagnosticBag Diagnostics) Analyze(
        string from, ProjectInfo project, MetadataReference[] references, string[] symbols, (string File, string Code)[] files, AuditFinding[]? findings = null)
    {
        var compilation = CSharpCompilation.Create(
            "Lib",
            files.Select(f => CSharpSyntaxTree.ParseText(f.Code, path: Path.Combine(Root, "src", "Lib", f.File))),
            [Corlib, .. references],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var bag = new DiagnosticBag();
        var result = SeamsAnalyzer.Analyze(new SeamsRequest
        {
            RepositoryRoot = Root,
            Project = project,
            UnportableFrom = from,
            Symbols = symbols,
            Findings = findings ?? [],
            Diagnostics = bag,
        }, compilation)!;
        return (result, bag);
    }

    private static (SeamsResult Result, DiagnosticBag Diagnostics) Analyze(
        string from, ProjectInfo project, MetadataReference[] references, string[] symbols, params (string File, string Code)[] files) =>
        Analyze(from, project, references, symbols, files, null);

    private static PortableExecutableReference Assembly(string name, string code)
    {
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(code)], [Corlib], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(image.ToArray());
    }
}

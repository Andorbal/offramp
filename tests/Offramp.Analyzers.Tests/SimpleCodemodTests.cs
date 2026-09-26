using Microsoft.CodeAnalysis;
using Offramp.Analyzers.CodeFixes.Rules;
using Offramp.Analyzers.Rules;

namespace Offramp.Analyzers.Tests;

/// <summary>Before/after pairs for the codemods that rewrite one expression or declaration at a time.</summary>
public sealed class SimpleCodemodTests
{
    [Fact]
    public Task Process_start_keeps_the_shell() => new CodemodTest<ProcessStartUrlAnalyzer, ProcessStartUrlFixer>(
        """
        using System.Diagnostics;

        class C
        {
            void M(string url)
            {
                [|Process.Start(url)|];
                [|Process.Start("notepad.exe", "a.txt")|];
                Process.Start(new ProcessStartInfo("x"));
            }
        }
        """,
        """
        using System.Diagnostics;

        class C
        {
            void M(string url)
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                Process.Start(new ProcessStartInfo("notepad.exe", "a.txt") { UseShellExecute = true });
                Process.Start(new ProcessStartInfo("x"));
            }
        }
        """).RunAsync(TestContext.Current.CancellationToken);

    [Fact]
    public Task Windows_time_zone_ids_go_through_tzconvert_and_iana_ids_stay() => new CodemodTest<TimeZoneIdsAnalyzer, TimeZoneIdsFixer>(
        """
        using System;

        class C
        {
            TimeZoneInfo Pacific() => [|TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time")|];

            TimeZoneInfo Chicago() => [|TimeZoneInfo.FindSystemTimeZoneById("America/Chicago")|];
        }
        """,
        """
        using System;

        class C
        {
            TimeZoneInfo Pacific() => TimeZoneConverter.TZConvert.GetTimeZoneInfo("Pacific Standard Time");

            TimeZoneInfo Chicago() => [|TimeZoneInfo.FindSystemTimeZoneById("America/Chicago")|];
        }
        """).RunAsync(TestContext.Current.CancellationToken);

    [Fact]
    public Task Sqlclient_usings_and_qualified_names_move_namespace() => new CodemodTest<SqlClientAnalyzer, SqlClientFixer>(
        """
        using [|System.Data.SqlClient|];

        class C
        {
            SqlConnection Open() => new SqlConnection("Server=.");

            object Command() => new [|System.Data.SqlClient|].SqlCommand();
        }
        """,
        """
        using Microsoft.Data.SqlClient;

        class C
        {
            SqlConnection Open() => new SqlConnection("Server=.");

            object Command() => new Microsoft.Data.SqlClient.SqlCommand();
        }
        """).RunAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Code_pages_are_registered_at_the_entry_point()
    {
        const string before = """
            using System.Text;

            static class Program
            {
                static void Main()
                {
                    var latin = Encoding.GetEncoding("iso-8859-1");
                    var windows = Encoding.GetEncoding(1252);
                }
            }
            """;
        var analyzer = new CodePagesAnalyzer();
        var fixer = new CodePagesFixer();

        var once = await DirectFix.ApplyAsync(analyzer, fixer, before, OutputKind.ConsoleApplication, TestContext.Current.CancellationToken);
        var twice = await DirectFix.ApplyAsync(analyzer, fixer, once, OutputKind.ConsoleApplication, TestContext.Current.CancellationToken);

        Assert.Equal("""
            using System.Text;

            static class Program
            {
                static void Main()
                {
                    // Code page encodings (offramp codemod codepages).
                    System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                    var latin = Encoding.GetEncoding("iso-8859-1");
                    var windows = Encoding.GetEncoding(1252);
                }
            }
            """, once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public Task Only_built_in_encodings_need_no_provider()
    {
        var test = new SitesTest<CodePagesAnalyzer>(
            """
            using System.Text;

            static class Program
            {
                static void Main()
                {
                    var utf8 = Encoding.GetEncoding("utf-8");
                    var latin = Encoding.GetEncoding(28591);
                }
            }
            """);
        test.TestState.OutputKind = OutputKind.ConsoleApplication;
        return test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task Service_controller_use_outside_a_service_is_reported() => new SitesTest<ServiceControllerAnalyzer>(
        """
        using System.ServiceProcess;

        class Monitor
        {
            bool Running() => [|new ServiceController("Spooler")|].Status == ServiceControllerStatus.Running;
        }

        class Heartbeat : ServiceBase
        {
            protected override void OnStart(string[] args) => new ServiceController("EventLog").Refresh();
        }
        """).RunAsync(TestContext.Current.CancellationToken);

    [Fact]
    public Task Assembly_attributes_the_sdk_generates_go() => new CodemodTest<AssemblyInfoAnalyzer, AssemblyInfoFixer>(
        """
        using System.Reflection;
        using System.Runtime.InteropServices;

        [assembly: [|AssemblyTitle("Shop")|]]
        [assembly: [|AssemblyVersion("1.2.3.4")|]]
        [assembly: ComVisible(false)]
        [assembly: AssemblyTrademark(""), [|AssemblyCompany("Contoso")|]]
        """,
        """
        using System.Reflection;
        using System.Runtime.InteropServices;

        [assembly: ComVisible(false)]
        [assembly: AssemblyTrademark("")]
        """).WithGlobalOptions("build_property.UsingMicrosoftNETSdk = true\n").RunAsync(TestContext.Current.CancellationToken);

    [Fact]
    public Task Assembly_attributes_stay_when_the_project_does_not_generate_them() => new CodemodTest<AssemblyInfoAnalyzer, AssemblyInfoFixer>(
        """
        using System.Reflection;

        [assembly: AssemblyTitle("Shop")]
        """,
        """
        using System.Reflection;

        [assembly: AssemblyTitle("Shop")]
        """).WithGlobalOptions("build_property.UsingMicrosoftNETSdk = true\nbuild_property.GenerateAssemblyInfo = false\n").RunAsync(TestContext.Current.CancellationToken);

    [Fact]
    public Task String_comparisons_become_ordinal_when_enabled() => new CodemodTest<StringComparisonAnalyzer, StringComparisonFixer>(
        """
        using System;

        class C
        {
            bool Starts(string s) => [|s.StartsWith("x")|];

            int Compare(string a, string b) => [|string.Compare(a, b, true)|];

            bool Same(string a, string b) => [|a.ToLower() == b.ToLower()|];

            bool Different(string a, string b) => [|a.ToUpperInvariant() != b.ToUpperInvariant()|];

            bool Char(string s) => s.StartsWith('x');
        }
        """,
        """
        using System;

        class C
        {
            bool Starts(string s) => s.StartsWith("x", StringComparison.Ordinal);

            int Compare(string a, string b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase);

            bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

            bool Different(string a, string b) => !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

            bool Char(string s) => s.StartsWith('x');
        }
        """).WithGlobalOptions("dotnet_diagnostic.OFRM009.severity = suggestion\n").RunAsync(TestContext.Current.CancellationToken);

    [Fact]
    public Task An_added_argument_keeps_its_separator_space_when_the_name_stays_qualified() => new CodemodTest<StringComparisonAnalyzer, StringComparisonFixer>(
        """
        class C
        {
            bool Starts(string s) => [|s.StartsWith("x")|];
        }
        """,
        """
        class C
        {
            bool Starts(string s) => s.StartsWith("x", System.StringComparison.Ordinal);
        }
        """).WithGlobalOptions("dotnet_diagnostic.OFRM009.severity = suggestion\n").RunAsync(TestContext.Current.CancellationToken);
}

using Microsoft.CodeAnalysis.Testing;
using Offramp.Analyzers.CodeFixes.Rules;
using Offramp.Analyzers.Rules;

namespace Offramp.Analyzers.Tests;

/// <summary>Before/after pairs for the codemods that add members or change several sites together.</summary>
public sealed class RestructuringCodemodTests
{
    [Fact]
    public Task Config_reads_use_an_injected_configuration() => new CodemodTest<ConfigManagerAnalyzer, ConfigManagerFixer>(
        """
        using System.Configuration;

        public interface IClock
        {
        }

        public class Sender
        {
            private readonly IClock _clock;

            public Sender(IClock clock)
            {
                _clock = clock;
            }

            public string Host() => [|ConfigurationManager.AppSettings["SmtpHost"]|];

            public string Database() => [|ConfigurationManager.ConnectionStrings["Main"].ConnectionString|];

            public static string Static() => [|ConfigurationManager.AppSettings["Static"]|];
        }
        """,
        """
        using System.Configuration;

        public interface IClock
        {
        }

        public class Sender
        {
            private readonly Microsoft.Extensions.Configuration.IConfiguration _configuration;
            private readonly IClock _clock;

            public Sender(IClock clock, Microsoft.Extensions.Configuration.IConfiguration configuration)
            {
                _configuration = configuration;
                _clock = clock;
            }

            public string Host() => _configuration["SmtpHost"];

            public string Database() => Microsoft.Extensions.Configuration.ConfigurationExtensions.GetConnectionString(_configuration, "Main");

            public static string Static() => [|ConfigurationManager.AppSettings["Static"]|];
        }
        """).RunAsync(TestContext.Current.CancellationToken);

    [Fact]
    public Task Config_reads_in_classes_created_with_new_are_left() => new SitesTest<ConfigManagerAnalyzer>(
        """
        using System.Configuration;

        public class Sender
        {
            public Sender(int port)
            {
            }

            public string Host() => {|#0:ConfigurationManager.AppSettings["SmtpHost"]|};
        }

        public class Program
        {
            public Sender Make() => new Sender(25);
        }
        """)
    {
        ExpectedDiagnostics =
        {
            new DiagnosticResult(Codemods.ConfigManager.Descriptor).WithLocation(0)
                .WithArguments("ConfigurationManager reads app.config, which modern .NET does not use; read IConfiguration instead. Not rewritten: the class is created with new at Test0.cs:14, which a new constructor parameter would break."),
        },
    }.RunAsync(TestContext.Current.CancellationToken);

    [Fact]
    public Task Http_context_uses_an_injected_accessor_with_the_system_web_adapters() => new CodemodTest<HttpContextAnalyzer, HttpContextFixer>(
        Adapters + """
        public class Greeter
        {
            private readonly string _name;

            public Greeter(string name)
            {
                _name = name;
            }

            public string Hello() => [|System.Web.HttpContext.Current|].User + _name;
        }
        """,
        Adapters + """
        public class Greeter
        {
            private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor _httpContextAccessor;
            private readonly string _name;

            public Greeter(string name, Microsoft.AspNetCore.Http.IHttpContextAccessor httpContextAccessor)
            {
                _httpContextAccessor = httpContextAccessor;
                _name = name;
            }

            public string Hello() => ((System.Web.HttpContext)_httpContextAccessor.HttpContext).User + _name;
        }
        """)
    { ReferenceAssemblies = ReferenceAssemblies.Net.Net80 }.RunAsync(TestContext.Current.CancellationToken);

    [Fact]
    public Task Http_context_without_asp_net_core_is_left() => new SitesTest<HttpContextAnalyzer>(
        """
        public class Greeter
        {
            public Greeter(string name)
            {
            }

            public string Who() => [|System.Web.HttpContext.Current|].User.Identity.Name;
        }
        """).RunAsync(TestContext.Current.CancellationToken);

    [Fact]
    public Task Webclient_downloads_in_async_methods_use_httpclient() => new CodemodTest<WebClientAnalyzer, WebClientFixer>(
        """
        using System.Net;
        using System.Threading.Tasks;

        class C
        {
            async Task<int> Length(string url)
            {
                using (var client = [|new WebClient()|])
                {
                    var text = client.DownloadString(url);
                    byte[] data = client.DownloadData(url);
                    return text.Length + client.DownloadString(url).Length + data.Length;
                }
            }

            string Sync(string url)
            {
                var client = [|new WebClient()|];
                return client.DownloadString(url);
            }
        }
        """,
        """
        using System.Net;
        using System.Threading.Tasks;

        class C
        {
            async Task<int> Length(string url)
            {
                using (var client = new System.Net.Http.HttpClient())
                {
                    var text = await client.GetStringAsync(url);
                    byte[] data = await client.GetByteArrayAsync(url);
                    return text.Length + (await client.GetStringAsync(url)).Length + data.Length;
                }
            }

            string Sync(string url)
            {
                var client = [|new WebClient()|];
                return client.DownloadString(url);
            }
        }
        """).RunAsync(TestContext.Current.CancellationToken);

    [Fact]
    public Task Javascript_serializer_calls_use_system_text_json() => new CodemodTest<JavaScriptSerializerAnalyzer, JavaScriptSerializerFixer>(
        """
        using System.Web.Script.Serialization;

        class C
        {
            string Write(object value)
            {
                var serializer = new JavaScriptSerializer();
                return [|serializer.Serialize(value)|];
            }

            T Read<T>(string json) => [|new JavaScriptSerializer().Deserialize<T>(json)|];
        }
        """,
        """
        using System.Web.Script.Serialization;

        class C
        {
            /// <summary>JavaScriptSerializer's behavior: names as declared, case-insensitive reads, fields included (offramp codemod javascript-serializer).</summary>
            private static readonly System.Text.Json.JsonSerializerOptions JavaScriptSerializerOptions = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true, IncludeFields = true };

            string Write(object value)
            {
                return System.Text.Json.JsonSerializer.Serialize<object>(value, JavaScriptSerializerOptions);
            }

            T Read<T>(string json) => System.Text.Json.JsonSerializer.Deserialize<T>(json, JavaScriptSerializerOptions);
        }
        """).RunAsync(TestContext.Current.CancellationToken);

    [Fact]
    public Task Binaryformatter_clones_round_trip_through_system_text_json() => new CodemodTest<BinaryFormatterCloneAnalyzer, BinaryFormatterCloneFixer>(
        """
        using System.IO;
        using System.Runtime.Serialization.Formatters.Binary;

        static class Cloner
        {
            public static T [|Clone|]<T>(T source)
            {
                var formatter = new BinaryFormatter();
                using (var stream = new MemoryStream())
                {
                    formatter.Serialize(stream, source);
                    stream.Position = 0;
                    return (T)formatter.Deserialize(stream);
                }
            }
        }
        """,
        """
        using System.IO;
        using System.Runtime.Serialization.Formatters.Binary;

        static class Cloner
        {
            /// <summary>Deep clones copy public properties and fields (offramp codemod binaryformatter-clone).</summary>
            private static readonly System.Text.Json.JsonSerializerOptions CloneOptions = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };

            public static T Clone<T>(T source)
            {
                return System.Text.Json.JsonSerializer.Deserialize<T>(System.Text.Json.JsonSerializer.Serialize<T>(source, CloneOptions), CloneOptions);
            }
        }
        """).RunAsync(TestContext.Current.CancellationToken);

    [Fact]
    public Task Thread_abort_becomes_cancellation() => new CodemodTest<ThreadAbortAnalyzer, ThreadAbortFixer>(
        """
        using System.Threading;

        class Worker
        {
            private Thread _thread;

            public void Start()
            {
                _thread = new Thread(Run);
                _thread.Start();
            }

            public void Stop()
            {
                [|_thread.Abort()|];
            }

            private void Run()
            {
                while (true)
                {
                    Thread.Sleep(1000);
                }
            }
        }
        """,
        """
        using System.Threading;

        class Worker
        {
            private readonly CancellationTokenSource _threadCancellation = new CancellationTokenSource();
            private Thread _thread;

            public void Start()
            {
                _thread = new Thread(Run);
                _thread.Start();
            }

            public void Stop()
            {
                _threadCancellation.Cancel();
            }

            private void Run()
            {
                while (!_threadCancellation.IsCancellationRequested)
                {
                    _threadCancellation.Token.WaitHandle.WaitOne(1000);
                }
            }
        }
        """).RunAsync(TestContext.Current.CancellationToken);

    /// <summary>ASP.NET Core's accessor and the System.Web adapters' conversion, as source.</summary>
    private const string Adapters = """
        namespace Microsoft.AspNetCore.Http
        {
            public class HttpContext
            {
            }

            public interface IHttpContextAccessor
            {
                HttpContext HttpContext { get; set; }
            }
        }

        namespace System.Web
        {
            public class HttpContext
            {
                public static HttpContext Current => null;

                public string User => "";

                public static implicit operator HttpContext(Microsoft.AspNetCore.Http.HttpContext context) => null;
            }
        }

        """;
}

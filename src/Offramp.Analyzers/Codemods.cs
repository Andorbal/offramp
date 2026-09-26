using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Offramp.Analyzers;

/// <summary>The targets a codemod's package is referenced for.</summary>
public enum CodemodPackageTargets
{
    /// <summary>Every target: the rewritten code needs the package everywhere.</summary>
    All,

    /// <summary>.NET Framework targets only: modern .NET has the API in the box.</summary>
    Framework,

    /// <summary>Modern .NET targets only: .NET Framework has the API in the box.</summary>
    Modern,
}

/// <summary>A package a codemod's rewrite needs.</summary>
public sealed class CodemodPackage
{
    public CodemodPackage(string id, string version, CodemodPackageTargets targets = CodemodPackageTargets.All)
    {
        Id = id;
        Version = version;
        Targets = targets;
    }

    public string Id { get; }

    public string Version { get; }

    public CodemodPackageTargets Targets { get; }
}

/// <summary>A codemod: its analyzer diagnostic, short name, and what its rewrite needs.</summary>
public sealed class Codemod
{
    private static readonly string[] OptInTags = ["OptIn"];

    public Codemod(string id, string name, string title, string description, bool optIn = false, bool experimental = false, params CodemodPackage[] packages)
    {
        Id = id;
        Name = name;
        Title = title;
        Description = description;
        OptIn = optIn;
        Experimental = experimental;
        Packages = packages.ToImmutableArray();
        Descriptor = new DiagnosticDescriptor(
            id,
            title,
            "{0}",
            "Offramp.Migration",
            DiagnosticSeverity.Info,
            isEnabledByDefault: !optIn,
            description: description,
            helpLinkUri: $"https://offramp.dev/codemods/{name}",
            customTags: optIn ? OptInTags : Array.Empty<string>());
    }

    public string Id { get; }

    public string Name { get; }

    public string Title { get; }

    public string Description { get; }

    /// <summary>Changes behavior on purpose: runs only when named, never with "all".</summary>
    public bool OptIn { get; }

    /// <summary>Right less than ~95% of the time on the fixtures: needs <c>--experimental</c>.</summary>
    public bool Experimental { get; }

    public ImmutableArray<CodemodPackage> Packages { get; }

    public DiagnosticDescriptor Descriptor { get; }
}

/// <summary>The codemod catalog (docs/spec/commands/codemod.md).</summary>
public static class Codemods
{
    /// <summary>The diagnostic property that says why a site is reported but not rewritten.</summary>
    public const string SkipReason = "OfframpSkipReason";

    public static readonly Codemod SqlClient = new(
        "OFRM001", "sqlclient", "Use Microsoft.Data.SqlClient",
        "System.Data.SqlClient is deprecated; Microsoft.Data.SqlClient is its successor. Usings and qualified names move to the new namespace and the package is added. Encrypt now defaults to true (OFR4510).",
        packages: new CodemodPackage("Microsoft.Data.SqlClient", "7.1.0"));

    public static readonly Codemod ConfigManager = new(
        "OFRM002", "config-manager", "Read settings from IConfiguration",
        "ConfigurationManager.AppSettings[\"key\"] in a class that takes its dependencies through its constructor becomes an injected IConfiguration[\"key\"].",
        packages: new CodemodPackage("Microsoft.Extensions.Configuration.Abstractions", "10.0.12"));

    public static readonly Codemod HttpContext = new(
        "OFRM003", "http-context", "Use IHttpContextAccessor",
        "HttpContext.Current in a class that takes its dependencies through its constructor becomes an injected IHttpContextAccessor.HttpContext, where the project references ASP.NET Core.");

    public static readonly Codemod WebClient = new(
        "OFRM004", "webclient", "Use HttpClient instead of WebClient",
        "A WebClient local used only for DownloadString, DownloadData, or UploadString inside an async method becomes an HttpClient with the awaited equivalents.");

    public static readonly Codemod JavaScriptSerializer = new(
        "OFRM005", "javascript-serializer", "Use System.Text.Json instead of JavaScriptSerializer",
        "JavaScriptSerializer.Serialize and Deserialize<T> become System.Text.Json.JsonSerializer calls with options that keep JavaScriptSerializer's behavior (names as declared, case-insensitive reads).",
        packages: new CodemodPackage("System.Text.Json", "10.0.12", CodemodPackageTargets.Framework));

    public static readonly Codemod BinaryFormatterClone = new(
        "OFRM006", "binaryformatter-clone", "Clone without BinaryFormatter",
        "A method that deep-clones by serializing to a MemoryStream with BinaryFormatter and reading it back becomes a System.Text.Json round trip.",
        packages: new CodemodPackage("System.Text.Json", "10.0.12", CodemodPackageTargets.Framework));

    public static readonly Codemod ThreadAbort = new(
        "OFRM007", "thread-abort", "Stop threads with cancellation instead of Thread.Abort",
        "Thread.Abort throws on modern .NET. A thread whose loop runs in the same type gets a CancellationTokenSource: the loop checks it and Abort becomes Cancel.",
        experimental: true);

    public static readonly Codemod ProcessStartUrl = new(
        "OFRM008", "process-start-url", "Keep UseShellExecute for Process.Start(string)",
        "Process.Start(fileName[, arguments]) used the shell on .NET Framework and does not on modern .NET, so URLs and documents stop opening. The call gets a ProcessStartInfo with UseShellExecute = true.");

    public static readonly Codemod StringComparison = new(
        "OFRM009", "string-comparison", "Make string comparisons ordinal",
        "Culture-sensitive StartsWith, EndsWith, IndexOf, string.Compare, and ToLower()/ToUpper() equality become ordinal comparisons. Changes behavior on purpose: opt-in.",
        optIn: true);

    public static readonly Codemod CodePages = new(
        "OFRM010", "codepages", "Register the code pages provider",
        "Modern .NET knows only Unicode, ASCII, and Latin-1 until the code pages provider is registered; the entry point registers it (the package supplies the provider on .NET Framework).",
        packages: new CodemodPackage("System.Text.Encoding.CodePages", "10.0.12", CodemodPackageTargets.Framework));

    public static readonly Codemod TimeZoneIds = new(
        "OFRM011", "timezone-ids", "Look up time zones with TZConvert",
        "A Windows time zone ID (\"Pacific Standard Time\") passed to FindSystemTimeZoneById fails on Linux without ICU; TZConvert.GetTimeZoneInfo accepts Windows and IANA IDs everywhere.",
        packages: new CodemodPackage("TimeZoneConverter", "7.2.0"));

    public static readonly Codemod ServiceController = new(
        "OFRM012", "service-controller", "Reference System.ServiceProcess.ServiceController",
        "Code that controls Windows services (ServiceController) outside a service needs the System.ServiceProcess.ServiceController package on modern .NET. No code changes; the package is added.",
        packages: new CodemodPackage("System.ServiceProcess.ServiceController", "10.0.12", CodemodPackageTargets.Modern));

    public static readonly Codemod AssemblyInfo = new(
        "OFRM013", "assemblyinfo", "Remove assembly attributes the SDK generates",
        "SDK-style projects generate AssemblyTitle, AssemblyVersion, AssemblyCompany, and the like; the same attributes in AssemblyInfo.cs are duplicates (CS0579) and go.");

    public static readonly ImmutableArray<Codemod> All = ImmutableArray.Create(
        SqlClient, ConfigManager, HttpContext, WebClient, JavaScriptSerializer, BinaryFormatterClone, ThreadAbort,
        ProcessStartUrl, StringComparison, CodePages, TimeZoneIds, ServiceController, AssemblyInfo);

    public static Codemod? ByName(string name) => All.FirstOrDefault(c => c.Name == name || c.Id == name);

    /// <summary>A diagnostic for a site: fixable, or with the reason it is not.</summary>
    public static Diagnostic Site(Codemod codemod, Location location, string message, string? skip = null) =>
        Diagnostic.Create(codemod.Descriptor, location,
            skip is null ? ImmutableDictionary<string, string?>.Empty : ImmutableDictionary<string, string?>.Empty.Add(SkipReason, skip),
            skip is null ? message : message + " Not rewritten: " + skip);

    public static bool IsSkipped(Diagnostic diagnostic) => diagnostic.Properties.ContainsKey(SkipReason);
}

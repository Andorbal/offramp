namespace Offramp.Core.Diagnostics;

public static partial class DiagnosticCatalog
{
    private const string AuditArea = "audit";

    public static readonly DiagnosticDescriptor OFR3001 = new(
        "OFR3001", Severity.Error,
        "API missing on target",
        "A type or member the project uses on .NET Framework does not exist in the target's reference assemblies (nor in the packages that support the target). The message names the API and its assembly's mapping from rules/framework-assemblies.yml.",
        "APIs from assemblies with no modern equivalent (System.Web), or in assemblies that moved to packages (System.Drawing.Common, System.Configuration.ConfigurationManager).",
        "See the assembly's mapping (`offramp deps gac`); replace the API or isolate it behind a seam.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3002 = new(
        "OFR3002", Severity.Warning,
        "API available only on Windows",
        "The API exists on the target but is marked [SupportedOSPlatform(\"windows\")], so it throws or is missing on Linux and macOS.",
        "Registry access, Windows-only Console members, Windows event logs, and similar APIs that survived the port only for Windows.",
        "Target netN-windows, guard the call with OperatingSystem.IsWindows(), or isolate it behind a seam.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3003 = new(
        "OFR3003", Severity.Error,
        "API throws on modern .NET",
        "The API compiles on modern .NET but throws PlatformNotSupportedException at run time.",
        "Thread.Abort, AppDomain.CreateDomain, CodeDom compilation, delegate BeginInvoke, and BinaryFormatter without the compatibility switch.",
        "These compile but throw PlatformNotSupportedException; replace them (cooperative cancellation, AssemblyLoadContext, Roslyn, System.Text.Json).",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3004 = new(
        "OFR3004", Severity.Error,
        "ASP.NET Web Forms",
        "The project uses ASP.NET Web Forms (System.Web.UI), which modern .NET does not have.",
        "Pages, user controls, and master pages deriving from System.Web.UI types.",
        "Web Forms has no port; rebuild pages in Razor Pages or Blazor (`offramp web inventory`), incrementally behind a YARP proxy.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3005 = new(
        "OFR3005", Severity.Error,
        "ASMX web services",
        "The project hosts ASMX web services, which modern .NET does not have.",
        "Classes deriving from WebService or marked [WebService]/[WebMethod].",
        "Move to ASP.NET Core controllers, or CoreWCF for SOAP clients that cannot change.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3006 = new(
        "OFR3006", Severity.Error,
        "WCF service host",
        "The project hosts WCF services, which modern .NET does not include (clients have packages; servers need CoreWCF).",
        "ServiceHost, ServiceHostFactory, or [ServiceBehavior] in the project.",
        "WCF clients have packages; servers move to CoreWCF, gRPC, or HTTP APIs.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3007 = new(
        "OFR3007", Severity.Error,
        ".NET Remoting",
        ".NET Remoting is gone on modern .NET.",
        "Types from System.Runtime.Remoting: MarshalByRefObject channels, RemotingConfiguration, remote activation.",
        "Remoting has no port; use gRPC, HTTP, or named pipes (`offramp remote`).",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3008 = new(
        "OFR3008", Severity.Error,
        "WF (Windows Workflow Foundation)",
        "Windows Workflow Foundation is not part of modern .NET.",
        "Types from System.Activities or System.Workflow.",
        "Workflow Foundation has no port (CoreWF is a community option); isolate workflows behind a seam.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3009 = new(
        "OFR3009", Severity.Error,
        "COM+, Code Access Security, or AppDomain sandboxing",
        "Enterprise Services (COM+), Code Access Security, and sandboxed AppDomains are gone on modern .NET.",
        "System.EnterpriseServices components, CAS permission attributes, PermissionSet, AllowPartiallyTrustedCallers.",
        "COM+ services, CAS permissions, and sandboxed AppDomains are gone; isolate the code in a separate process.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3010 = new(
        "OFR3010", Severity.Warning,
        "project not compiled against the target",
        "The project could not be compiled against the target's reference assemblies, so audit api reports no missing (OFR3001) or Windows-only (OFR3002) APIs for it. The message carries NuGet's or MSBuild's error.",
        "The target's reference packs could not be restored (no network, a feed that requires authentication, an SDK too old for the target).",
        "Fix the restore error the message names (feeds, credentials, SDK version) and run the audit again.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3011 = new(
        "OFR3011", Severity.Info,
        "packages without target support left out",
        "Some of the project's packages have no assets for the target, so the target compilation leaves them out and the APIs used from them show up as missing (OFR3001).",
        "Packages that only ever shipped .NET Framework assemblies (for example Microsoft.AspNet.Mvc or Microsoft.Web.Infrastructure).",
        "Run `offramp deps audit` for replacements; the APIs used from these packages are the ones to port.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3101 = new(
        "OFR3101", Severity.Warning,
        "culture-sensitive string operation",
        "A string comparison, search, or case mapping uses the current culture implicitly; modern .NET uses ICU on every platform, which compares and matches differently from NLS on .NET Framework.",
        "string.Compare, IndexOf(string), StartsWith(string), ToUpper(), or OrderBy over strings without a StringComparison, CultureInfo, or comparer.",
        "Pass StringComparison.Ordinal (or a CultureInfo) explicitly; ICU on Linux and modern .NET compares and matches differently from NLS.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3102 = new(
        "OFR3102", Severity.Warning,
        "non-Unicode code page",
        "Encoding.GetEncoding asks for a legacy code page, which modern .NET only provides after CodePagesEncodingProvider is registered.",
        "Windows-1252, Shift-JIS, or other non-Unicode code pages requested by number or name.",
        "Register CodePagesEncodingProvider.Instance (System.Text.Encoding.CodePages) at startup before asking for legacy code pages.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3103 = new(
        "OFR3103", Severity.Warning,
        "path assumes Windows separators or folders",
        "A path uses Windows separators, drive letters, or a Windows-only special folder; other file systems use '/' and are case-sensitive.",
        "Hard-coded backslashes or drive letters passed to System.IO, Environment.SpecialFolder members that exist only on Windows.",
        "Use Path.Combine with relative segments and Path.DirectorySeparatorChar; file systems elsewhere are case-sensitive and use '/'.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3104 = new(
        "OFR3104", Severity.Warning,
        "time zone looked up by Windows ID",
        "A time zone is looked up by a Windows ID; other platforms use IANA IDs.",
        "TimeZoneInfo.FindSystemTimeZoneById(\"Eastern Standard Time\") or an ID read from data.",
        "Use IANA IDs, or TimeZoneInfo.TryConvertWindowsIdToIanaId (.NET 6+) where Windows IDs come from data.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3105 = new(
        "OFR3105", Severity.Warning,
        "registry access",
        "The code reads or writes the Windows registry.",
        "Microsoft.Win32.Registry and RegistryKey.",
        "Move settings to configuration; guard any remaining registry access with OperatingSystem.IsWindows().",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3106 = new(
        "OFR3106", Severity.Warning,
        "ambient ASP.NET context",
        "The code relies on ASP.NET's ambient request or hosting context, which ASP.NET Core does not have.",
        "HttpContext.Current, HttpRuntime, HostingEnvironment.",
        "ASP.NET Core has no ambient context; pass HttpContext (or IHttpContextAccessor) and IWebHostEnvironment explicitly.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3107 = new(
        "OFR3107", Severity.Warning,
        "shell execution through Process.Start",
        "Process.Start with a file or URL relies on the shell; UseShellExecute defaults to false on modern .NET.",
        "Process.Start(\"https://...\") or Process.Start(\"report.pdf\").",
        "UseShellExecute defaults to false on modern .NET; open URLs and documents with new ProcessStartInfo(target) { UseShellExecute = true }.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3108 = new(
        "OFR3108", Severity.Warning,
        "legacy SQL Server client (System.Data.SqlClient)",
        "System.Data.SqlClient is superseded by Microsoft.Data.SqlClient, whose defaults differ (Encrypt is true from 4.0).",
        "SqlConnection, SqlCommand, and friends from System.Data.SqlClient.",
        "Move to Microsoft.Data.SqlClient; Encrypt defaults to true from 4.0, so connection strings may need TrustServerCertificate.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3109 = new(
        "OFR3109", Severity.Info,
        "floating-point ToString without a format",
        "double and float ToString() without a format give the shortest round-trippable string since .NET Core 3.0, so output can change.",
        "Formatting floating-point values for display, storage, or comparison without a format string.",
        "Since .NET Core 3.0, ToString() gives the shortest round-trippable string; pass a format (\"G15\", \"R\", \"F2\") where output is compared or stored.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3110 = new(
        "OFR3110", Severity.Warning,
        "obsolete networking API",
        "WebRequest, WebClient, and ServicePointManager are obsolete on modern .NET.",
        "HTTP calls through HttpWebRequest or WebClient, global settings through ServicePointManager.",
        "Use HttpClient (with SocketsHttpHandler settings instead of ServicePointManager).",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3111 = new(
        "OFR3111", Severity.Warning,
        "ambient principal",
        "The ambient principal does not flow the same way on modern .NET.",
        "Thread.CurrentPrincipal used for authorization, WindowsIdentity.",
        "Thread.CurrentPrincipal does not flow the same way; ASP.NET Core uses HttpContext.User, services an explicit identity.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3112 = new(
        "OFR3112", Severity.Info,
        "settings read through ConfigurationManager",
        "ConfigurationManager settings come from the host's app.config through a compatibility package, not from web.config or IConfiguration.",
        "ConfigurationManager.AppSettings and ConnectionStrings.",
        "The System.Configuration.ConfigurationManager package reads the host's app.config, not web.config; consider IConfiguration (`offramp config convert`).",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3113 = new(
        "OFR3113", Severity.Info,
        "regular expression without a timeout",
        "A regular expression runs without a match timeout.",
        "new Regex(pattern) or Regex.IsMatch(input, pattern) without a TimeSpan.",
        "Pass a match timeout (or RegexOptions.NonBacktracking) for patterns applied to request data.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3114 = new(
        "OFR3114", Severity.Warning,
        "TLS 1.0/1.1 or SSL pinned",
        "The code pins SSL 3.0, TLS 1.0, or TLS 1.1, which modern defaults reject.",
        "SslProtocols.Tls, SecurityProtocolType.Tls11, and similar values set explicitly.",
        "Let the operating system choose (SslProtocols.None / SecurityProtocolType.SystemDefault); old protocols are rejected by modern defaults.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3115 = new(
        "OFR3115", Severity.Warning,
        "assembly loading",
        "Assembly.LoadFrom, LoadFile, and AppDomain.AssemblyResolve follow AssemblyLoadContext rules on modern .NET.",
        "Plug-in loading and custom assembly probing.",
        "Assembly.LoadFrom/LoadFile and AppDomain.AssemblyResolve follow AssemblyLoadContext rules on modern .NET; review load contexts and probing.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3116 = new(
        "OFR3116", Severity.Info,
        "runtime settings in app.config",
        "Garbage collector and threading settings in app.config or web.config are ignored on modern .NET; they belong in runtimeconfig.json.",
        "<gcServer>, <gcConcurrent>, <GCCpuGroup>, and similar elements under <runtime>.",
        "GC and runtime settings move to runtimeconfig.json (or MSBuild properties such as ServerGarbageCollection).",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3117 = new(
        "OFR3117", Severity.Info,
        "URL encoding differences",
        "HttpUtility and Uri.EscapeUriString escape differently from WebUtility and Uri.EscapeDataString.",
        "URL encoding whose output is persisted, compared, or signed.",
        "HttpUtility and Uri.EscapeUriString escape differently from WebUtility and Uri.EscapeDataString; compare outputs where they are persisted or signed.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3118 = new(
        "OFR3118", Severity.Warning,
        "machine key or Forms authentication",
        "MachineKey and Forms authentication have no direct equivalent; ASP.NET Core uses Data Protection.",
        "MachineKey.Protect/Unprotect, FormsAuthentication tickets and cookies.",
        "ASP.NET Core Data Protection replaces MachineKey; sharing Forms authentication cookies needs a compatibility adapter.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3119 = new(
        "OFR3119", Severity.Info,
        "timers and thread pool tuning",
        "Timers and thread pool tuning usually work, but services moving to containers should revisit them.",
        "System.Timers.Timer in services, ThreadPool.SetMinThreads.",
        "Usually fine; services heading to containers should prefer PeriodicTimer or hosted services, and revisit thread pool minimums.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3120 = new(
        "OFR3120", Severity.Info,
        "operating system check",
        "An operating system check assumes Windows.",
        "Environment.OSVersion or RuntimeInformation.IsOSPlatform branches.",
        "Branches that assume Windows need review; prefer OperatingSystem.IsWindows() and friends.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3201 = new(
        "OFR3201", Severity.Error,
        "insecure serializer",
        "BinaryFormatter and its relatives are insecure, and .NET 9 removed them (they throw).",
        "BinaryFormatter, SoapFormatter, NetDataContractSerializer, ObjectStateFormatter, LosFormatter.",
        "BinaryFormatter and its relatives are removed (throw) from .NET 9; migrate the data, using the System.Runtime.Serialization.Formatters compatibility package only while a dual-read migration runs.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3202 = new(
        "OFR3202", Severity.Info,
        "transient binary serialization (deep clone)",
        "A binary formatter round-trips an object through a MemoryStream in one member (a deep clone); the data never leaves the process.",
        "The Clone idiom: Serialize into a new MemoryStream, rewind, Deserialize.",
        "The data never leaves the process; replace the round trip with a copy constructor, a record `with`, or a System.Text.Json round trip (`offramp codemod`).",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3203 = new(
        "OFR3203", Severity.Error,
        "persisted or transported binary serialization",
        "Binary-formatter data leaves the process: a file, a stream parameter or field, or a memory stream whose bytes are returned or stored.",
        "Persisting objects to disk, sending them over a network, caching them, or storing them in session state.",
        "Data written with BinaryFormatter outlives the process; plan a dual-read migration to a safe format before the target drops the formatter.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3204 = new(
        "OFR3204", Severity.Info,
        "type serialized with a binary formatter",
        "A type is serialized with a binary formatter; the finding lists whether it implements ISerializable, has deserialization callbacks, or holds delegates.",
        "Types passed to Serialize (followed through object parameters to call sites) or cast from Deserialize.",
        "Each listed type needs a new serialized shape; ISerializable, OnDeserialized hooks, and delegate members need attention.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3205 = new(
        "OFR3205", Severity.Info,
        "[Serializable] type never serialized",
        "A [Serializable] type is never passed to a binary formatter anywhere in the audited solution.",
        "Attributes added by habit or left over from removed serialization.",
        "No serializer in the solution receives it; the attribute can stay.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3210 = new(
        "OFR3210", Severity.Warning,
        "legacy JSON serializer",
        "JavaScriptSerializer and DataContractJsonSerializer are legacy JSON serializers.",
        "System.Web.Script.Serialization.JavaScriptSerializer, DataContractJsonSerializer.",
        "Move to System.Text.Json (or Newtonsoft.Json where its behaviors are relied on).",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3211 = new(
        "OFR3211", Severity.Info,
        "XML serializer",
        "XmlSerializer works on modern .NET; pre-generated serializers (sgen) need Microsoft.XmlSerializer.Generator.",
        "XmlSerializer usages, especially with GenerateSerializationAssemblies.",
        "Works on the target; pre-generated serializers (sgen) need Microsoft.XmlSerializer.Generator or can be dropped.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3301 = new(
        "OFR3301", Severity.Info,
        "P/Invoke declaration",
        "An inventory entry for a P/Invoke declaration: library, entry point, calling convention, character set, SetLastError, marshalled types, and whether the library exists only on Windows.",
        "Any [DllImport] method.",
        "Check each library exists on every target platform; Windows system libraries do not.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3302 = new(
        "OFR3302", Severity.Warning,
        "ANSI string marshalling by default",
        "A P/Invoke marshals strings with the ANSI default (or CharSet.Auto, which is ANSI off Windows).",
        "[DllImport] with string, char, or StringBuilder parameters and no CharSet.Unicode or [MarshalAs].",
        "CharSet defaults to ANSI (Auto means Unicode only on Windows); declare CharSet.Unicode or marshal strings explicitly.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3303 = new(
        "OFR3303", Severity.Info,
        "candidate for [LibraryImport]",
        "A P/Invoke has a blittable signature, so [LibraryImport] can generate its marshalling at compile time.",
        "[DllImport] methods over integers, pointers, and handles only.",
        "A blittable signature can use [LibraryImport] for source-generated marshalling on .NET 7+.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3310 = new(
        "OFR3310", Severity.Warning,
        "COM interop",
        "The project uses COM, which exists only on Windows.",
        "COMReference items, [ComImport] interfaces, Marshal.GetActiveObject.",
        "COM works only on Windows; isolate it behind a seam or target netN-windows.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3320 = new(
        "OFR3320", Severity.Info,
        "structured exception interop",
        "Structured exception and HRESULT interop differ across platforms.",
        "Marshal.GetHRForException, catching SEHException.",
        "SEHException and HRESULT mapping differ across platforms; review the handling.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3601 = new(
        "OFR3601", Severity.Warning,
        "member cannot be wrapped in `#if`",
        "`ifdef wrap` left a finding unwrapped: code that also compiles on the target needs the member (it is referenced outside the wrapped code, overrides or implements a member, or shares its lines with other code), so it needs a real port rather than a conditional.",
        "A helper with a missing API that callers on both targets use; an interface implementation that uses one.",
        "Port the member (or put the missing API behind a seam), or wrap its callers first and run `ifdef wrap` again.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3602 = new(
        "OFR3602", Severity.Warning,
        "finding does not match the source",
        "The location an audit finding names no longer holds the symbol it reports, so `ifdef wrap` does not touch it.",
        "The file changed after the audit ran.",
        "Run the audit again (`offramp scan`, then `offramp audit api --format json --out audit.json`) and wrap from the new findings.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3603 = new(
        "OFR3603", Severity.Warning,
        "conditional region depends on other symbols",
        "`ifdef strip` left an `#if` chain alone: with the stripped symbol decided, which branch compiles still depends on other symbols.",
        "Conditions such as `NETFRAMEWORK && DEBUG` or `#elif` branches on other symbols before the one that names the stripped symbol.",
        "Simplify the condition by hand, or strip the other symbol first.",
        AuditArea);

    public static readonly DiagnosticDescriptor OFR3604 = new(
        "OFR3604", Severity.Error,
        "findings file missing or invalid",
        "`ifdef wrap --findings` could not read an audit result from the file.",
        "A wrong path, or a file that is not the output of `offramp audit api --format json` (or its `--json` envelope).",
        "Write the findings with `offramp audit api --format json --out audit.json` and pass that file.",
        AuditArea);
}

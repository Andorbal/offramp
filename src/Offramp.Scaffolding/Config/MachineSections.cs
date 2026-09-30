namespace Offramp.Scaffolding.Config;

/// <summary>A section .NET Framework declares in machine.config, and what becomes of it on .NET.</summary>
/// <param name="InCode">True when the setting still matters and is made in code on .NET (OFR4407); false when .NET has nothing to set.</param>
/// <param name="Note">What to do instead, for the section's note and the diagnostic.</param>
public sealed record MachineSection(bool InCode, string Note);

/// <summary>
/// The sections and section groups that .NET Framework's machine.config declares, so a
/// configuration file uses them without declaring them in configSections
/// (docs/decisions/0060-config-convert-reads-section-groups-and-machine-config-sections.md).
/// .NET reads none of them from a file. <c>appSettings</c>, <c>connectionStrings</c>,
/// <c>runtime</c>, <c>startup</c>, <c>system.diagnostics</c>, <c>system.serviceModel</c>,
/// <c>system.web</c>, <c>system.webServer</c>, and <c>system.web.extensions</c> have their own
/// handling in <see cref="ConfigConverter"/>.
/// </summary>
public static class MachineSections
{
    private static readonly Dictionary<string, MachineSection> Sections = new(StringComparer.Ordinal)
    {
        ["system.net"] = new(true, "network settings are made in code on .NET: connection limits and proxies on the HttpClient's handler (SocketsHttpHandler.MaxConnectionsPerServer, Proxy, or the HTTP_PROXY, HTTPS_PROXY, and NO_PROXY variables), mailSettings on SmtpClient's properties; ServicePointManager settings do not apply to HttpClient."),
        ["system.data"] = new(true, "ADO.NET provider factories are registered in code on .NET: call DbProviderFactories.RegisterFactory(invariantName, factory) for each one at startup."),
        ["system.transactions"] = new(true, "transaction timeouts are set in code on .NET: TransactionManager.DefaultTimeout and MaxTimeout, or the TransactionScope's own timeout."),
        ["system.runtime.caching"] = new(true, "MemoryCache limits are passed in code on .NET: to the MemoryCache constructor's settings, or to MemoryCacheOptions with Microsoft.Extensions.Caching.Memory."),
        ["system.runtime.serialization"] = new(true, "known types for DataContractSerializer are passed in code on .NET: DataContractSerializerSettings.KnownTypes, or [KnownType] attributes."),
        ["system.identityModel"] = new(true, "Windows Identity Foundation is not on .NET: configure the ASP.NET Core authentication handlers (WS-Federation, OpenID Connect, JWT bearer) in code."),
        ["system.identityModel.services"] = new(true, "Windows Identity Foundation is not on .NET: configure the ASP.NET Core authentication handlers (WS-Federation, OpenID Connect, JWT bearer) in code."),
        ["system.runtime.remoting"] = new(true, ".NET Remoting is not on .NET: the calls need another transport (gRPC, HTTP; `offramp remote` scaffolds one)."),
        ["configBuilders"] = new(true, "configuration builders feed ConfigurationManager; on .NET each is a configuration provider added in code (AddEnvironmentVariables, AddUserSecrets, AddAzureKeyVault)."),
        ["system.codedom"] = new(false, "Compilers for ASP.NET's run-time compilation of pages and views: ASP.NET Core compiles Razor at build time, so nothing replaces it."),
        ["system.serviceModel.activation"] = new(false, "WCF activation in IIS: CoreWCF services are hosted by ASP.NET Core."),
        ["system.xaml.hosting"] = new(false, "Workflow and XAML hosting in IIS, which .NET does not have."),
        ["system.xml.serialization"] = new(false, "XmlSerializer's temporary files and date handling, which .NET does not configure."),
        ["system.windows.forms"] = new(false, "Windows Forms settings: the DPI mode is the project's ApplicationHighDpiMode property (or Application.SetHighDpiMode) on .NET, and JIT debugging has no setting."),
        ["system.data.dataset"] = new(false, "DataSet settings, which .NET does not read from configuration."),
        ["system.data.odbc"] = new(false, "ODBC provider settings, which .NET does not read from configuration."),
        ["system.data.oledb"] = new(false, "OLE DB provider settings, which .NET does not read from configuration."),
        ["system.data.oracleclient"] = new(false, "System.Data.OracleClient settings: the provider is not on .NET (Oracle.ManagedDataAccess.Core takes its settings in code)."),
        ["system.data.sqlclient"] = new(false, "SqlClient settings, which .NET does not read from configuration (Microsoft.Data.SqlClient takes them in the connection string or in code)."),
        ["uri"] = new(false, "IDN and IRI parsing: .NET always parses them, so there is nothing to set."),
        ["mscorlib"] = new(false, ".NET Framework runtime settings that .NET does not read."),
        ["satelliteassemblies"] = new(false, ".NET Framework runtime settings that .NET does not read."),
        ["windows"] = new(false, ".NET Framework runtime settings that .NET does not read."),
    };

    /// <summary>The machine.config section of this name, or null.</summary>
    public static MachineSection? Find(string name) => Sections.GetValueOrDefault(name);
}

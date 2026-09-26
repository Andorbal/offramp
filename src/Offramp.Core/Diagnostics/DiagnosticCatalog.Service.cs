namespace Offramp.Core.Diagnostics;

public static partial class DiagnosticCatalog
{
    private const string ServiceArea = "service";

    public static readonly DiagnosticDescriptor OFR4101 = new(
        "OFR4101", Severity.Warning,
        "pause, continue, or custom commands differ",
        "The service handles pause, continue, or custom commands; the generic host has none of them, so the worker keeps them as methods the application can call and nothing calls them by default.",
        "Services that pause work while an operator investigates, or accept custom commands from `sc control`.",
        "Decide whether the behavior is still needed; call the worker's Pause and Continue from an endpoint or configuration if it is.",
        ServiceArea);

    public static readonly DiagnosticDescriptor OFR4102 = new(
        "OFR4102", Severity.Warning,
        "code left in compatibility region",
        "The worker keeps service code that uses ServiceBase members the host does not have (RequestAdditionalTime, ExitCode, Stop, the EventLog object, a Topshelf host control) inside `#if OFFRAMP_SERVICEBASE`, which is never defined; the code does not run until someone ports it.",
        "Shutdown timing requests, exit codes, self-stopping services, event log sources.",
        "Port each region: HostOptions.ShutdownTimeout for more stop time, IHostApplicationLifetime.StopApplication to stop, Environment.ExitCode for exit codes.",
        ServiceArea);

    public static readonly DiagnosticDescriptor OFR4103 = new(
        "OFR4103", Severity.Info,
        "multiple services in one executable",
        "The executable runs several services; the worker project hosts one worker per service in one process, which registers as one Windows service or one container.",
        "`ServiceBase.Run(new ServiceBase[] { ... })` with more than one service.",
        "Keep them together, or split the worker project if they must start, stop, or scale independently.",
        ServiceArea);

    public static readonly DiagnosticDescriptor OFR4104 = new(
        "OFR4104", Severity.Warning,
        "service depends on other Windows services",
        "The installer or Topshelf configuration makes the service depend on other Windows services; a container or systemd unit has no such dependency, so the worker must wait for or reach the dependency itself.",
        "Dependencies on the event log, SQL Server, MSMQ, or a vendor service.",
        "Replace the dependency with readiness checks or retries; the install script keeps it for the Windows host.",
        ServiceArea);

    public static readonly DiagnosticDescriptor OFR4105 = new(
        "OFR4105", Severity.Warning,
        "session change or power events",
        "The service reacts to logon sessions or power events, which the generic host does not deliver; the handler is kept in an excluded region.",
        "Services that act on user logon, lock, or system suspend.",
        "Keep this part as a Windows service (the Windows host), or drop the behavior for containers.",
        ServiceArea);

    public static readonly DiagnosticDescriptor OFR4106 = new(
        "OFR4106", Severity.Warning,
        "generated worker does not compile",
        "The worker project (the generated code and the linked files it uses) was compiled in memory for the target and has errors; it is still written, with the errors to fix.",
        "Linked code that uses .NET Framework-only APIs, the ServiceProcess types in kept code, or project types from other projects.",
        "Fix the listed errors in the worker, or port the linked code first (`audit api` lists what is missing).",
        ServiceArea);

    public static readonly DiagnosticDescriptor OFR4107 = new(
        "OFR4107", Severity.Error,
        "no service found",
        "`service --project` names a project with no ServiceBase subclass and no Topshelf HostFactory configuration.",
        "A library, a console application, or a service hosted by another framework.",
        "Pass the project that contains the service's entry point.",
        ServiceArea);

    public static readonly DiagnosticDescriptor OFR4108 = new(
        "OFR4108", Severity.Error,
        "worker directory exists",
        "`service` writes a new project only: its output directory already exists and is not empty, so nothing was generated.",
        "Running `service` twice, or an --out that points at an existing project.",
        "Pass another --out, or delete the earlier output.",
        ServiceArea);
}

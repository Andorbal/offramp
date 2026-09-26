namespace Offramp.Core.Diagnostics;

public static partial class DiagnosticCatalog
{
    private const string WebArea = "web";

    public static readonly DiagnosticDescriptor OFR4201 = new(
        "OFR4201", Severity.Warning,
        "unported Web Forms page",
        "Web Forms pages, user controls, and master pages have no ASP.NET Core counterpart that code can be converted to; `web scaffold` inventories them and leaves them to the legacy application behind the proxy.",
        "Any .aspx, .ascx, or .master file.",
        "Keep the page behind the proxy, rewrite it as a Razor Page or Blazor component, or use a third-party Web Forms converter.",
        WebArea);

    public static readonly DiagnosticDescriptor OFR4202 = new(
        "OFR4202", Severity.Info,
        "handler became an unmapped endpoint stub",
        "The handler's ProcessRequest is kept in a marked region of a minimal API endpoint stub, which is not mapped: its path keeps going to the legacy application through the proxy until someone ports the code and maps the endpoint.",
        "Image, file, and feed handlers (.ashx, *.axd registrations).",
        "Port ProcessRequest into the stub's Handle method, then uncomment its MapMethods line in Program.cs.",
        WebArea);

    public static readonly DiagnosticDescriptor OFR4203 = new(
        "OFR4203", Severity.Error,
        "scaffolded project does not compile",
        "The generated ASP.NET Core project was compiled in memory and still has errors after the actions the compiler rejected were left to the legacy application; it is written anyway.",
        "Code shared with the legacy application that uses System.Web, or types the linked files need from other projects.",
        "Read the errors in the message; move the shared code into a project both applications reference, or port it.",
        WebArea);

    public static readonly DiagnosticDescriptor OFR4204 = new(
        "OFR4204", Severity.Error,
        "output folder not empty",
        "The folder `--new` names already has files, so nothing was generated.",
        "A second run.",
        "Pass another --new, or delete the folder.",
        WebArea);
}

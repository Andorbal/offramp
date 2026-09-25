namespace Offramp.Core.Diagnostics;

public static partial class DiagnosticCatalog
{
    private const string DependenciesArea = "deps";

    public static readonly DiagnosticDescriptor OFR1006 = new(
        "OFR1006", Severity.Warning,
        "feed unreachable; result partial",
        "A NuGet feed could not be queried, so any answer that depends on it is incomplete.",
        "No network, a feed that is down, or missing credentials for a private feed.",
        "Check `nuget.config`, network access, and credential providers, then re-run.",
        DependenciesArea);
}

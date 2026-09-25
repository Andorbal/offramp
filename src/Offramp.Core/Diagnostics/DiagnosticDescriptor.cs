namespace Offramp.Core.Diagnostics;

/// <summary>
/// The stable definition of a diagnostic code. Every code Offramp emits has a
/// descriptor in <see cref="DiagnosticCatalog"/>, and <c>docs/diagnostics.md</c>
/// is generated from those descriptors.
/// </summary>
/// <param name="Code">The stable code, <c>OFR####</c>.</param>
/// <param name="DefaultSeverity">Severity unless the emitting command or <c>offramp.yml</c> says otherwise.</param>
/// <param name="Title">One line, lower case, no trailing period; used in tables.</param>
/// <param name="Meaning">What the diagnostic means.</param>
/// <param name="Cause">The typical cause.</param>
/// <param name="Fix">What to do about it.</param>
/// <param name="Area">The command group or subsystem that emits it.</param>
public sealed record DiagnosticDescriptor(
    string Code,
    Severity DefaultSeverity,
    string Title,
    string Meaning,
    string Cause,
    string Fix,
    string Area)
{
    public const string HelpBaseUri = "https://offramp.dev/diagnostics/";

    public string HelpUri => HelpBaseUri + Code;
}

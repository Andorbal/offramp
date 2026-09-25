namespace Offramp.Fixtures;

/// <summary>
/// Marks a test that triggers a diagnostic code. A meta-test fails when a code in
/// <c>DiagnosticCatalog</c> is not named by any test (CLAUDE.md, non-negotiable 6).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class ProducesDiagnosticAttribute(string code) : Attribute
{
    public string Code { get; } = code;
}

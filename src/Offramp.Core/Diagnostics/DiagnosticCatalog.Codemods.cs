namespace Offramp.Core.Diagnostics;

public static partial class DiagnosticCatalog
{
    private const string CodemodsArea = "codemod";

    public static readonly DiagnosticDescriptor OFR4501 = new(
        "OFR4501", Severity.Info,
        "codemod site skipped",
        "The codemod found a site it does not rewrite safely; the message says why (a synchronous method, a static member, a class created with new, ...). The code is unchanged.",
        "Patterns just outside what the codemod proves safe.",
        "Change the site by hand, or change the code around it so the codemod can, and run it again.",
        CodemodsArea);

    public static readonly DiagnosticDescriptor OFR4502 = new(
        "OFR4502", Severity.Error,
        "unknown codemod",
        "`codemod run --mod` names no codemod in the catalog.",
        "A typo, or an ID from a newer version.",
        "Run `offramp codemod list` for the names and IDs.",
        CodemodsArea);

    public static readonly DiagnosticDescriptor OFR4503 = new(
        "OFR4503", Severity.Error,
        "codemod is experimental",
        "The codemod is right less than about 95% of the time on the fixtures and corpus, so it runs only with --experimental.",
        "Codemods whose rewrite changes behavior in ways that need review (thread-abort).",
        "Pass --experimental and review every change before committing it.",
        CodemodsArea);

    public static readonly DiagnosticDescriptor OFR4504 = new(
        "OFR4504", Severity.Error,
        "source changed since the last scan",
        "A file the codemod would rewrite differs from the text recorded by the last scan, so it was left alone: rewriting it would lose the newer edits.",
        "Editing files after `offramp scan`.",
        "Run `offramp scan` and the codemod again.",
        CodemodsArea);

    public static readonly DiagnosticDescriptor OFR4505 = new(
        "OFR4505", Severity.Warning,
        "package not added",
        "The rewritten code needs a package that could not be added to the project file (a packages.config project, or no central PackageVersion file found under central package management).",
        "Old-style projects, central package management with an unusual layout.",
        "Add the package the message names by hand, or modernize the project first (`offramp csproj modernize`).",
        CodemodsArea);

    public static readonly DiagnosticDescriptor OFR4506 = new(
        "OFR4506", Severity.Warning,
        "project does not reference Offramp.Analyzers",
        "`--format-mode` runs `dotnet format analyzers`, which only sees analyzers the project references; this project does not reference the Offramp.Analyzers package, so it was skipped.",
        "Using --format-mode before adding the analyzer package.",
        "Add `<PackageReference Include=\"Offramp.Analyzers\" PrivateAssets=\"all\" />`, or run without --format-mode.",
        CodemodsArea);

    public static readonly DiagnosticDescriptor OFR4507 = new(
        "OFR4507", Severity.Error,
        "verification failed; codemod rolled back",
        "The build (or verification command) failed after the codemod, and `verify.onFailure: rollback` restored every file from the journal.",
        "A rewrite that needs a package the project cannot restore, or a build that was already broken.",
        "Read the verification errors; fix them or skip the sites, then run the codemod again. `verify.onFailure: keep` leaves the change in place.",
        CodemodsArea);

    public static readonly DiagnosticDescriptor OFR4508 = new(
        "OFR4508", Severity.Error,
        "dotnet format failed",
        "`--format-mode` ran `dotnet format analyzers` for the project and it failed: an exit code other than 0 (or 2, which a dry run returns when there are changes), or no report.",
        "A project that does not restore or load, or an SDK without `dotnet format`.",
        "Run the command in the message yourself to see its output; `dotnet restore` the project first, or run without --format-mode.",
        CodemodsArea);

    public static readonly DiagnosticDescriptor OFR4510 = new(
        "OFR4510", Severity.Info,
        "connections encrypted by default (Microsoft.Data.SqlClient)",
        "Microsoft.Data.SqlClient defaults Encrypt to true (System.Data.SqlClient defaulted to false), so connections to servers without a trusted certificate now fail.",
        "Development and on-premises SQL Servers with self-signed certificates.",
        "Install a trusted certificate on the server, or set TrustServerCertificate=True (or Encrypt=False) in the connection strings that need it.",
        CodemodsArea);
}

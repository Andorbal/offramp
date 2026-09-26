using System.Globalization;
using System.Text;

namespace Offramp.NuGet.Audit;

/// <summary><c>deps audit --format markdown</c>: the table for an issue or a wiki page.</summary>
public static class AuditMarkdown
{
    public static string Write(DepsAuditResult result)
    {
        var builder = new StringBuilder();
        var s = result.Summary;
        builder.Append(CultureInfo.InvariantCulture, $"# Package audit for {result.Target}\n\n");
        builder.Append(CultureInfo.InvariantCulture, $"{result.Packages.Count} packages: {s.Ok} ok, {s.Upgrade} upgrade, {s.Replace} replace, {s.Blocked} blocked, {s.Unknown} unknown.");
        if (result.Partial)
        {
            builder.Append(" Some feeds could not be reached; unknown answers are incomplete.");
        }

        builder.Append("\n\n| Package | In use | Supports target | Lowest supporting | Newest supporting | Status | Notes |\n");
        builder.Append("|---|---|---|---|---|---|---|\n");
        foreach (var package in Ordered(result.Packages))
        {
            var inUse = string.Join(", ", package.InUse.Select(v => v.Version + (v.Pinned ? " (pinned)" : "")));
            var supports = string.Join(", ", package.SupportsTarget.InUseVersions.Select(v => $"{v.Key}: {(v.Value is null ? "?" : v.Value.Value ? "yes" : "no")}"));
            var notes = new List<string>();
            if (package.Replacement is not null)
            {
                notes.Add("successor: " + package.Replacement.Replacement);
            }

            if (package.Deprecated is not null)
            {
                notes.Add("deprecated" + (package.Deprecated.AlternateId is null ? "" : $" (use {package.Deprecated.AlternateId})"));
            }

            if (package.WindowsOnly)
            {
                notes.Add("Windows only");
            }

            builder.Append(CultureInfo.InvariantCulture,
                $"| {Cell(package.Id)} | {Cell(inUse)} | {Cell(supports)} | {package.LowestSupporting ?? "none"} | {package.NewestSupporting ?? "none"} | {package.Status.ToString().ToLowerInvariant()} | {Cell(string.Join("; ", notes))} |\n");
        }

        return builder.ToString();
    }

    /// <summary>Blocked first, then replace, upgrade, unknown, ok; within a status, the most projects first.</summary>
    public static IEnumerable<PackageAudit> Ordered(IEnumerable<PackageAudit> packages) =>
        packages
            .OrderBy(p => p.Status switch
            {
                PackageStatus.Blocked => 0,
                PackageStatus.Replace => 1,
                PackageStatus.Upgrade => 2,
                PackageStatus.Unknown => 3,
                _ => 4,
            })
            .ThenByDescending(p => p.InUse.SelectMany(v => v.Projects).Distinct(StringComparer.Ordinal).Count())
            .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase);

    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}

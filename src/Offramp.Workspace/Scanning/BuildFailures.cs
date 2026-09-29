using System.Globalization;
using System.Text.Json.Nodes;
using Offramp.Workspace.Ingest;

namespace Offramp.Workspace.Scanning;

/// <summary>How <c>scan</c> summarizes a failed build (<c>OFR0130</c>) and names what stopped it.</summary>
internal static class BuildFailures
{
    private const int MaxErrorsInMessage = 5;

    /// <summary>
    /// An error of the NuGet restore, which runs before the build: a NuGet code (<c>NU1xxx</c>), or an error
    /// without a code logged from the restore targets (a package download that failed).
    /// </summary>
    public static bool IsRestoreError(BuildError error) =>
        error.Code.StartsWith("NU1", StringComparison.Ordinal)
        || ((error.Code.Length == 0 || error.Code.StartsWith("NU", StringComparison.Ordinal))
            && error.File is not null
            && string.Equals(Path.GetFileName(error.File.Replace('\\', '/')), "NuGet.targets", StringComparison.OrdinalIgnoreCase));

    /// <summary>The error's code, or <c>restore</c> or <c>no code</c> for one without a code.</summary>
    public static string Label(BuildError error) =>
        error.Code.Length > 0 ? error.Code : IsRestoreError(error) ? "restore" : "no code";

    /// <summary>The first line of an error message, which is the part that says what failed.</summary>
    public static string FirstLine(string message) =>
        message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";

    /// <summary>
    /// <c>OFR0130</c>'s message and data. Errors count once per project: 25 projects failing in one shared targets
    /// file are 25 errors of one code in 25 projects, not one error. The texts shown are distinct.
    /// </summary>
    /// <param name="errors">The log's errors, with capture paths.</param>
    /// <param name="relative">Makes a capture path repository-relative, or null outside the repository.</param>
    /// <param name="scrub">Makes the capture paths inside a message repository-relative.</param>
    public static (string Message, List<KeyValuePair<string, JsonNode?>> Data) Summarize(
        IReadOnlyList<BuildError> errors, Func<string, string?> relative, Func<string, string> scrub)
    {
        var distinct = errors.DistinctBy(e => (Label(e), e.ProjectFile, e.File, e.Line, e.Message)).ToList();
        var texts = distinct
            .Select(e => $"{(e.File is null ? "" : (relative(e.File) ?? Path.GetFileName(e.File)) + (e.Line is null ? "" : $"({e.Line})") + ": ")}{Label(e)}: {scrub(e.Message)}")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Most first, then by code: one cause (a missing import, a letter case) often makes most of them.
        var byCode = distinct
            .GroupBy(Label, StringComparer.Ordinal)
            .Select(g => (Code: g.Key, Count: g.Count(), Projects: g.Select(e => e.ProjectFile ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count()))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Code, StringComparer.Ordinal)
            .ToList();
        var counts = string.Join(", ", byCode.Select(g => string.Create(CultureInfo.InvariantCulture,
            $"{g.Code} ×{g.Count}{(g.Projects > 1 ? $" in {g.Projects} projects" : "")}")));
        var message = distinct.Count == 0
            ? "The build failed; the model is partial."
            : string.Create(CultureInfo.InvariantCulture, $"The build failed with {distinct.Count} error(s) ({counts}); the model is partial. First: {string.Join("; ", texts.Take(MaxErrorsInMessage))}");
        return (message,
        [
            KeyValuePair.Create<string, JsonNode?>("errorCount", distinct.Count),
            KeyValuePair.Create<string, JsonNode?>("byCode", new JsonObject(byCode.Select(g => KeyValuePair.Create<string, JsonNode?>(g.Code, g.Count)))),
            KeyValuePair.Create<string, JsonNode?>("projectsByCode", new JsonObject(byCode.Select(g => KeyValuePair.Create<string, JsonNode?>(g.Code, g.Projects)))),
            KeyValuePair.Create<string, JsonNode?>("errors", new JsonArray([.. texts.Take(20).Select(e => (JsonNode?)e)])),
        ]);
    }

    /// <summary>Why nothing was built when the restore, which runs first, failed; null when it did not.</summary>
    public static string? RestoreFailed(IReadOnlyList<BuildError> errors, Func<string, string> scrub) =>
        errors.FirstOrDefault(IsRestoreError) is { } restore
            ? $"not built: the restore failed, so MSBuild built nothing ({Label(restore)}: {scrub(FirstLine(restore.Message))})"
            : null;

    /// <summary>
    /// Why nothing was built when no project was evaluated at all, such as MSB4249 on a Web Site project, which
    /// stops the solution first; null when a project was.
    /// </summary>
    public static string? NothingBuilt(IReadOnlyList<BuildError> errors, int evaluatedProjects, Func<string, string> scrub) =>
        evaluatedProjects == 0 && errors.Count > 0
            ? $"not built: MSBuild stopped before building any project ({Label(errors[0])}: {scrub(FirstLine(errors[0].Message))})"
            : null;
}

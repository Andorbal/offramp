using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Offramp.Core.Diagnostics;
using Offramp.Core.Json;

namespace Offramp.Core.Output;

/// <summary>The <c>offramp</c> header of every JSON envelope.</summary>
public sealed record EnvelopeHeader
{
    public required string Version { get; init; }

    /// <summary>The command path as typed, for example <c>deps audit</c>.</summary>
    public required string Command { get; init; }

    /// <summary>The target framework moniker, for example <c>net10.0</c>.</summary>
    public required string Target { get; init; }

    /// <summary>The only absolute path in the document.</summary>
    public required string RepositoryRoot { get; init; }

    public string? Solution { get; init; }

    public string? WorkspaceHash { get; init; }

    /// <summary>UTC, second precision, ISO 8601 (<c>2026-09-25T20:11:04Z</c>).</summary>
    public required string StartedAt { get; init; }

    public required long DurationMs { get; init; }

    public required JsonNode EffectiveConfig { get; init; }

    public static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>
/// Writes the JSON envelope defined in <c>docs/spec/01-cli-conventions.md</c>:
/// <c>$schema</c>, <c>offramp</c>, <c>result</c>, <c>diagnostics</c>, <c>summary</c>.
/// </summary>
public static class EnvelopeWriter
{
    public const string SchemaUri = "https://offramp.dev/schemas/v1/envelope.json";

    public static string Write<TResult>(
        EnvelopeHeader header,
        TResult? result,
        JsonTypeInfo<TResult> resultType,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        var sorted = diagnostics.OrderBy(d => d, DiagnosticOrder.Instance).ToList();
        var summary = new DiagnosticSummary(
            sorted.Count(d => d.Severity == Severity.Error),
            sorted.Count(d => d.Severity == Severity.Warning),
            sorted.Count(d => d.Severity == Severity.Info));

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, OfframpJson.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("$schema", SchemaUri);
            writer.WritePropertyName("offramp");
            JsonSerializer.Serialize(writer, header, OfframpCoreJsonContext.Default.EnvelopeHeader);
            writer.WritePropertyName("result");
            if (result is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                JsonSerializer.Serialize(writer, result, resultType);
            }

            writer.WritePropertyName("diagnostics");
            JsonSerializer.Serialize(writer, sorted, OfframpCoreJsonContext.Default.ListDiagnostic);
            writer.WritePropertyName("summary");
            JsonSerializer.Serialize(writer, summary, OfframpCoreJsonContext.Default.DiagnosticSummary);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}

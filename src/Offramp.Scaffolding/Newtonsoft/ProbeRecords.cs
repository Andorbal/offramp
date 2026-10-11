using System.Text.Json.Serialization;
using Offramp.Core.Json;

namespace Offramp.Scaffolding.Newtonsoft;

[JsonConverter(typeof(CamelCaseEnumConverter<ProbeStatus>))]
public enum ProbeStatus
{
    /// <summary>Recording: the probe's baseline is new or changed.</summary>
    Recorded,

    /// <summary>Recording: the baseline already says what the probe observed.</summary>
    Unchanged,

    /// <summary>Recording: the two runs disagree; no baseline is written (OFR3738).</summary>
    Quarantined,

    /// <summary>Verifying: the observation equals the baseline.</summary>
    Same,

    /// <summary>Verifying: returned in both, with different values (OFR3762).</summary>
    Differs,

    /// <summary>Verifying: returned in the baseline, throws now (OFR3762).</summary>
    NowThrows,

    /// <summary>Verifying: threw in the baseline, returns now (OFR3762).</summary>
    NoLongerThrows,

    /// <summary>Verifying: throws the same exception type with another message (OFR3763).</summary>
    MessageOnly,

    /// <summary>The probe wrote no result (OFR3741).</summary>
    Missing,

    /// <summary>Verifying: there is nothing to compare with (OFR3744).</summary>
    NoBaseline,
}

/// <summary>One probe's outcome in a run, compared with its baseline or the other run.</summary>
public sealed record ProbeOutcome
{
    public required string Probe { get; init; }

    public required string TestProject { get; init; }

    /// <summary><c>netfx</c> or <c>core</c>.</summary>
    public required string Runtime { get; init; }

    public required ProbeStatus Status { get; init; }

    /// <summary>The Newtonsoft.Json assembly version that ran, or <c>none</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Newtonsoft { get; init; }

    /// <summary>A line diff, baseline first, for differences and quarantines.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Diff { get; init; }
}

/// <summary>A probe's result file (<c># offramp-newtonsoft-probe 1</c>; ADR 0067).</summary>
internal sealed record ProbeRecord(string Probe, string Newtonsoft, string Outcome, string Body)
{
    public const string Format = "# offramp-newtonsoft-probe 1";

    /// <summary>What is compared: the outcome and the body, not the header that names the release.</summary>
    public string Observation => "outcome: " + Outcome + "\n---\n" + Body;

    public bool Threw => Outcome == "threw";

    /// <summary>For a probe that threw: the exception's type (the first line of the body).</summary>
    public string? Exception => Threw ? Body.Split('\n', 2)[0] : null;

    public static ProbeRecord? Parse(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var separator = normalized.IndexOf("\n---\n", StringComparison.Ordinal);
        if (!normalized.StartsWith(Format + "\n", StringComparison.Ordinal) || separator < 0)
        {
            return null;
        }

        var header = normalized[..separator].Split('\n').Skip(1)
            .Select(l => l.Split(": ", 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
        return header.TryGetValue("probe", out var probe) && header.TryGetValue("outcome", out var outcome)
            ? new ProbeRecord(probe, header.GetValueOrDefault("newtonsoft", "none"), outcome, normalized[(separator + 5)..].TrimEnd('\n'))
            : null;
    }

    /// <summary>How an observation compares with its baseline.</summary>
    public static ProbeStatus Compare(ProbeRecord baseline, ProbeRecord now) => (baseline.Threw, now.Threw) switch
    {
        _ when baseline.Observation == now.Observation => ProbeStatus.Same,
        (false, true) => ProbeStatus.NowThrows,
        (true, false) => ProbeStatus.NoLongerThrows,
        (true, true) when baseline.Exception == now.Exception => ProbeStatus.MessageOnly,
        _ => ProbeStatus.Differs,
    };

    /// <summary>The lines that differ, as <c>-</c> (baseline) and <c>+</c> (now), in order.</summary>
    public static string Diff(string before, string after)
    {
        var a = before.Split('\n');
        var b = after.Split('\n');
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var lines = new List<string>();
        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y])
            {
                x++;
                y++;
            }
            else if (x < a.Length && (y == b.Length || lcs[x + 1, y] >= lcs[x, y + 1]))
            {
                lines.Add("- " + a[x++]);
            }
            else
            {
                lines.Add("+ " + b[y++]);
            }
        }

        return string.Join("\n", lines);
    }
}

using System.Text.Json.Serialization;
using Offramp.Core.Json;

namespace Offramp.Workspace.Doctor;

[JsonConverter(typeof(CamelCaseEnumConverter<CheckStatus>))]
public enum CheckStatus
{
    Pass,
    Warn,
    Fail,
    Skip,
}

/// <summary>One environment or repository check, with a remedy when it did not pass.</summary>
public sealed record DoctorCheck
{
    /// <summary>Stable identifier, for example <c>dotnet-sdk</c>.</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required CheckStatus Status { get; init; }

    public required string Message { get; init; }

    public string? Remedy { get; init; }

    /// <summary>Diagnostic codes this check reported, sorted.</summary>
    public IReadOnlyList<string> Codes { get; init; } = [];
}

public sealed record GlobalJsonInfo(string Path, string? Version, string? RollForward);

public sealed record GitInfo(string? Version, bool Repository);

/// <summary>What doctor found about the machine.</summary>
public sealed record DoctorEnvironment
{
    /// <summary>Installed SDK versions, ascending.</summary>
    public IReadOnlyList<string> Sdks { get; init; } = [];

    /// <summary>The SDK <c>dotnet</c> selects in the repository (honoring global.json).</summary>
    public string? SelectedSdk { get; init; }

    public GlobalJsonInfo? GlobalJson { get; init; }

    public required GitInfo Git { get; init; }

    /// <summary>Runtime identifier of the machine, for example <c>linux-x64</c>.</summary>
    public required string Os { get; init; }

    public required string Target { get; init; }
}

public sealed record DoctorSummary(int Pass, int Warn, int Fail, int Skip);

/// <summary>The <c>result</c> of <c>offramp doctor</c> (<c>schemas/v1/doctor.json</c>).</summary>
public sealed record DoctorReport
{
    public required IReadOnlyList<DoctorCheck> Checks { get; init; }

    public required DoctorEnvironment Environment { get; init; }

    public required DoctorSummary Summary { get; init; }

    /// <summary>The compile-only fix with <c>--fix</c>; null otherwise.</summary>
    public CompileOnlyFix? Fix { get; init; }
}

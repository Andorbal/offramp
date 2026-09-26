using Offramp.Core.Diagnostics;
using Offramp.Refactoring.ChangeSets;
using Offramp.Workspace.Targets;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Scaffolding.Service;

public sealed record ServiceRequest
{
    public required string RepositoryRoot { get; init; }

    /// <summary>The service's executable project.</summary>
    public required ProjectInfo Project { get; init; }

    /// <summary>The target major version: the worker targets <c>netN.0</c>.</summary>
    public int TargetMajor { get; init; } = 10;

    /// <summary><c>linux</c> (a container), <c>windows</c> (a Windows service), or <c>both</c>.</summary>
    public string Host { get; init; } = "linux";

    /// <summary>The worker project's directory; default <c>NAME.Worker</c> next to the project.</summary>
    public string? OutputDirectory { get; init; }

    public bool Dockerfile { get; init; }

    public bool Kubernetes { get; init; }

    public bool Health { get; init; }

    /// <summary><c>json-console</c> or <c>simple</c>.</summary>
    public string Logging { get; init; } = "json-console";

    /// <summary>Resolves the worker's references for the trial compilation; null skips it.</summary>
    public TargetReferenceResolver? References { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }
}

/// <summary>A timer that drives the service's work.</summary>
public sealed record ServiceTimer
{
    /// <summary>The field or local that holds it.</summary>
    public required string Name { get; init; }

    /// <summary><c>System.Timers.Timer</c> or <c>System.Threading.Timer</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The interval expression in milliseconds, when it is known.</summary>
    public string? Interval { get; init; }

    /// <summary>The method its ticks run.</summary>
    public string? Callback { get; init; }

    /// <summary>Converted to a <c>PeriodicTimer</c> loop in the worker.</summary>
    public bool Converted { get; init; }
}

/// <summary>A Windows service found in the project.</summary>
public sealed record DetectedService
{
    /// <summary><c>servicebase</c> or <c>topshelf</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The class, fully qualified.</summary>
    public required string Type { get; init; }

    public required string ServiceName { get; init; }

    public string? DisplayName { get; init; }

    public string? Description { get; init; }

    /// <summary><c>LocalSystem</c>, <c>LocalService</c>, <c>NetworkService</c>, or <c>User</c>; null when not set.</summary>
    public string? Account { get; init; }

    /// <summary><c>Automatic</c>, <c>Manual</c>, <c>Disabled</c>, or <c>AutomaticDelayed</c>; null when not set.</summary>
    public string? StartType { get; init; }

    /// <summary>Windows services it depends on (OFR4104).</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>The lifecycle members it has: OnStart, OnStop, OnPause, ...; WhenStarted, WhenStopped for Topshelf.</summary>
    public IReadOnlyList<string> Lifecycle { get; init; } = [];

    public IReadOnlyList<ServiceTimer> Timers { get; init; } = [];

    /// <summary>How it logs: EventLog, Trace, Debug, log4net, NLog, Serilog.</summary>
    public IReadOnlyList<string> Logging { get; init; } = [];

    /// <summary>The <c>ConfigurationManager</c> keys it reads (see <c>config convert</c>).</summary>
    public IReadOnlyList<string> Configuration { get; init; } = [];

    /// <summary>The generated worker, fully qualified.</summary>
    public required string Worker { get; init; }
}

/// <summary>Something the worker no longer needs, reported for removal from the service project.</summary>
public sealed record ServiceRemoval(string Path, string Reason);

/// <summary>The generated project.</summary>
public sealed record WorkerProject(string Path, string Sdk, string TargetFramework);

/// <summary>The <c>result</c> of <c>offramp service</c> (<c>schemas/v1/service.json</c>).</summary>
public sealed record ServiceResult
{
    public required string Project { get; init; }

    public required string Host { get; init; }

    public required IReadOnlyList<DetectedService> Services { get; init; }

    public WorkerProject? Worker { get; init; }

    /// <summary>Source files the worker compiles from the service project (links, not copies).</summary>
    public IReadOnlyList<string> LinkedSources { get; init; } = [];

    /// <summary>Every file written, repository-relative.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    public IReadOnlyList<ServiceRemoval> Removals { get; init; } = [];

    public IReadOnlyList<string> NextSteps { get; init; } = [];

    public bool Applied { get; init; }

    public string? Journal { get; init; }

    public string? Preview { get; init; }
}

public sealed record ServicePlan(ServiceResult Result, ChangeSet? ChangeSet);

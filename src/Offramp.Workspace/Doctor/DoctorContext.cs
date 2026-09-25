using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Git;
using Offramp.Core.Paths;
using Offramp.Core.Processes;
using Offramp.Core.Progress;
using Offramp.Workspace.Environment;

namespace Offramp.Workspace.Doctor;

/// <summary>Everything the doctor checks read. Services are injectable so tests can simulate machines.</summary>
public sealed record DoctorContext
{
    public required RepositoryRoot Repository { get; init; }

    public required ConfigLoadResult Config { get; init; }

    /// <summary>Absolute path of the workspace model the commands would read.</summary>
    public required string WorkspacePath { get; init; }

    public required IProcessRunner Processes { get; init; }

    public required IGitService Git { get; init; }

    public required IReferenceAssembliesProbe ReferenceAssemblies { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }

    public IProgressSink Progress { get; init; } = NullProgressSink.Instance;

    /// <summary>Runtime identifier reported in the result.</summary>
    public string Os { get; init; } = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
}

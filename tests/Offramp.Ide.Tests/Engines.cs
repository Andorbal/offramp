using Offramp.Core.Caching;
using Offramp.Core.Configuration;
using Offramp.Core.Git;
using Offramp.Core.Processes;
using Offramp.Fixtures;
using Offramp.Workspace.Store;
using Offramp.Workspace.Targets;

namespace Offramp.Ide.Tests;

/// <summary>Engines over scanned fixtures, with real git and real processes.</summary>
internal static class Engines
{
    public const string Fixture = "ide-counterpart";
    public const string Foo = "src/Foo/Foo.csproj";
    public const string Legacy = "src/Legacy/Legacy.csproj";
    public const string ModernF = "src/ModernF/ModernF.csproj";
    public const string Shared = "src/Shared/Shared.csproj";

    public static Task<IdeEngine> CreateAsync(ScannedFixture fixture, IdeSettings? settings = null, OfframpConfig? config = null) =>
        IdeEngine.CreateAsync(new IdeEngineOptions
        {
            RepositoryRoot = fixture.Root,
            Model = WorkspaceStore.Read(fixture.WorkspacePath),
            WorkspacePath = fixture.WorkspacePath,
            Config = config ?? new OfframpConfig(),
            Settings = settings ?? new IdeSettings(),
            Git = new GitService(ProcessRunner.Instance),
            Processes = ProcessRunner.Instance,
            References = new TargetReferenceResolver(fixture.Root, ProcessRunner.Instance, NullCache.Instance),
        }, CancellationToken.None);

    /// <summary>Legacy's portable code lives in ModernF.</summary>
    public static IdeSettings LegacyMapped { get; } = new()
    {
        ProjectMap = [new ProjectMapEntry { From = "Legacy", To = "ModernF" }],
    };
}

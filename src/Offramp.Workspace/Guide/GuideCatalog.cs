using Offramp.Core.Model;
using Offramp.Workspace.Model;

namespace Offramp.Workspace.Guide;

/// <summary>One step of the guide: an Offramp command, why it matters, and the fact that decides whether it applies.</summary>
public sealed record GuideStep
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>For someone new to the migration; <c>{target}</c> becomes the target framework.</summary>
    public required string Why { get; init; }

    /// <summary>Arguments after <c>offramp</c>; <c>{project}</c>, <c>{tfms}</c>, and <c>{new}</c> are filled per project.</summary>
    public required IReadOnlyList<string> Command { get; init; }

    public GuideWrites Writes { get; init; }

    /// <summary>Steps that must be done, skipped, or not needed first.</summary>
    public IReadOnlyList<string> Requires { get; init; } = [];

    /// <summary>Exit code 1 means the step is not done (doctor's failing checks), not "findings".</summary>
    public bool FailsOnFindings { get; init; }

    /// <summary>Decided from the repository alone: records are ignored, and it cannot be skipped or marked done.</summary>
    public bool ObservedOnly { get; init; }

    /// <summary>Done because the repository shows it, whatever was recorded.</summary>
    public Func<GuideFacts, bool>? Observed { get; init; }

    /// <summary>Whether the step is useful for this repository; needs the workspace model. Null: always.</summary>
    public Func<GuideFacts, WorkspaceModel, bool>? Applies { get; init; }

    /// <summary>The candidate projects of a step done per project; needs the workspace model.</summary>
    public Func<GuideFacts, WorkspaceModel, IEnumerable<ProjectInfo>>? Projects { get; init; }

    /// <summary>A note from the repository's state and the step's last record.</summary>
    public Func<GuideFacts, GuideRecord?, string?>? Note { get; init; }

    /// <summary>For a step done per project with no candidates: why it is blocked rather than not needed, or null.</summary>
    public Func<GuideFacts, WorkspaceModel, string?>? BlockedWithoutProjects { get; init; }

    public bool PerProject => Projects is not null;
}

public sealed record GuideStage(string Id, string Title, IReadOnlyList<GuideStep> Steps);

/// <summary>
/// The guide's checklist (docs/spec/commands/guide.md#steps): Offramp's commands in the order a
/// migration usually needs them. Deliberately a fixed list with one simple fact per step; when
/// several steps are open, the guide asks instead of ranking them (docs/decisions/0028-guide.md).
/// </summary>
public static class GuideCatalog
{
    private static readonly string[] TestAssemblies =
    [
        "Microsoft.VisualStudio.QualityTools.UnitTestFramework", "Microsoft.VisualStudio.TestPlatform.TestFramework",
        "nunit.framework", "TUnit.Core", "xunit.core", "xunit.v3.core",
    ];

    private static readonly ProjectKind[] Applications =
        [ProjectKind.Console, ProjectKind.Service, ProjectKind.Web, ProjectKind.Winforms, ProjectKind.Wpf];

    public static IReadOnlyList<GuideStage> Stages { get; } =
    [
        new("setup", "Get set up",
        [
            new GuideStep
            {
                Id = "doctor",
                Title = "Check the machine and the repository",
                Why = "Offramp needs a .NET SDK that can build {target}, the .NET Framework reference assemblies (so .NET Framework code compiles on any OS), and git (so moves show up as renames). Doctor checks these and your repository and says how to fix whatever is missing.",
                Command = ["doctor"],
                FailsOnFindings = true,
                Note = (_, record) => record is { Status: GuideRecordStatus.Failed }
                    ? "Doctor found a failing check. Fix it and run doctor again, or mark this step done if the check does not matter here."
                    : null,
            },
            new GuideStep
            {
                Id = "init",
                Title = "Write offramp.yml",
                Why = "offramp.yml records the target .NET version, which solution to work on, how changes are verified, and package versions you must keep. Every command reads it. On a terminal, init asks a few questions with the detected answers as defaults, and it adds Offramp's scratch files to .gitignore.",
                Command = ["init"],
                Writes = GuideWrites.Config,
                Requires = ["doctor"],
                Observed = facts => facts.ConfigExists,
            },
            new GuideStep
            {
                Id = "scan",
                Title = "Build the workspace model",
                Why = "Offramp builds your solution once with a binary log and reads everything else from it: projects, references, packages, and exactly what the compiler saw. Every later step reads this model, so scan again whenever project files change; the guide notices when the model is out of date.",
                Command = ["scan"],
                Writes = GuideWrites.State,
                Requires = ["init"],
                ObservedOnly = true,
                Observed = facts => facts.ModelFresh,
                Note = ScanNote,
            },
            new GuideStep
            {
                Id = "compile-only",
                Title = "Let macOS and Linux compile projects with Windows-only build steps",
                Why = "Some projects run build steps that only work on Windows (sgen, COM references, T4, SQL projects, build events). A small block in Directory.Build.props skips those steps outside Windows so the code still compiles for analysis and for your IDE. Builds on Windows do not change.",
                Command = ["doctor", "--fix"],
                Writes = GuideWrites.Repository,
                Requires = ["doctor", "scan"],
                Observed = facts => facts.CompileOnlyPresent,
                Applies = (_, model) => model.Projects.Any(p => p.WindowsOnlyBuildSteps.Count > 0),
            },
        ]),
        new("understand", "See what you have",
        [
            new GuideStep
            {
                Id = "plan",
                Title = "See the order to port projects in",
                Why = "A project can move to {target} only after the .NET Framework projects it depends on. plan sorts them into waves: wave 1 can be ported today (the frontier), wave 2 right after, and so on. Projects in a reference cycle share a wave, and the cycle has to be broken first.",
                Command = ["plan", "--waves"],
            },
            new GuideStep
            {
                Id = "graph",
                Title = "Look at the dependency graph",
                Why = "The same projects as a picture: what depends on what, colored by framework. The terminal shows a summary; `offramp graph --format html --out graph.html` writes a page you can open offline and show to others.",
                Command = ["graph"],
            },
            new GuideStep
            {
                Id = "deps-audit",
                Title = "Check your NuGet packages against {target}",
                Why = "Packages are often what holds a migration up. For each package this shows whether the version you use supports {target}, the lowest version that does, and a successor when the package is abandoned or Windows-only.",
                Command = ["deps", "audit"],
                Applies = (_, model) => model.Packages.Count > 0,
            },
            new GuideStep
            {
                Id = "audit-api",
                Title = "Find APIs that are missing on {target}",
                Why = "Lists every use of an API that does not exist on {target} or throws there (System.Web, AppDomain, remoting, WCF services, ...), with what to use instead. This is most of the work of porting a project, so it is worth knowing its size early.",
                Command = ["audit", "api"],
            },
            new GuideStep
            {
                Id = "audit-behavior",
                Title = "Find code that compiles but behaves differently",
                Why = "Some code compiles on both and does something else at runtime: culture-sensitive string comparison, Windows time zone IDs, Process.Start with a URL, System.Data.SqlClient defaults. This audit finds those places so tests can cover them before you switch.",
                Command = ["audit", "behavior"],
            },
            new GuideStep
            {
                Id = "audit-serialization",
                Title = "Find binary serialization",
                Why = "BinaryFormatter is gone in .NET 9 and later. This finds it and tells the harmless deep-clone idiom apart from data written to files, caches, or the network, which needs a plan for reading old data.",
                Command = ["audit", "serialization"],
            },
            new GuideStep
            {
                Id = "audit-native",
                Title = "Find native interop and COM",
                Why = "P/Invoke declarations and COM usage tie code to Windows or change marshalling on {target}. This lists them so you know which code needs Windows or a different approach.",
                Command = ["audit", "native"],
            },
            new GuideStep
            {
                Id = "audit-dead-code",
                Title = "Find code nobody uses",
                Why = "Code that nothing calls does not need porting. Each candidate has a confidence level; deleting the high-confidence ones before you start makes everything after it smaller.",
                Command = ["audit", "dead-code"],
            },
            new GuideStep
            {
                Id = "web-inventory",
                Title = "Take stock of each ASP.NET application",
                Why = "An ASP.NET application does not port by adding a target: it moves to ASP.NET Core piece by piece. The inventory lists its controllers, routes, filters, modules, handlers, session and authentication use, so you can see what that involves.",
                Command = ["web", "inventory", "--project", "{project}"],
                Projects = (_, model) => model.Projects.Where(p => p.Kind == ProjectKind.Web && p.FrameworkClass == FrameworkClass.Framework),
            },
            new GuideStep
            {
                Id = "report",
                Title = "Make a progress report",
                Why = "A summary for stakeholders: how much is portable, which applications are blocked by what, and what can be ported today. Every scan adds a point to the burn-down, so run it again as you go; `offramp report --out report.html` writes a page to share.",
                Command = ["report"],
            },
        ]),
        new("prepare", "Tidy up while still on .NET Framework",
        [
            new GuideStep
            {
                Id = "move-tests",
                Title = "Move test code out of production projects",
                Why = "Tests kept inside a production project pull test frameworks into production and have to be ported with it. This moves test files, byte for byte and with git mv, into a test project (created next to it when there is none).",
                Command = ["move", "tests", "--project", "{project}", "--create"],
                Writes = GuideWrites.Repository,
                Projects = (_, model) => model.Projects.Where(p => p.Kind != ProjectKind.Test && ReferencesTestFramework(p)),
            },
            new GuideStep
            {
                Id = "csproj-modernize",
                Title = "Convert legacy project files to SDK style",
                Why = "SDK-style project files are short, use PackageReference, and can target .NET Framework and {target} at the same time, which is how projects are ported one at a time. The conversion is checked by building both versions and comparing what the compiler gets.",
                Command = ["csproj", "modernize", "--all"],
                Writes = GuideWrites.Repository,
                Applies = (_, model) => model.Projects.Any(p => !p.SdkStyle && p.Language == "csharp"),
            },
            new GuideStep
            {
                Id = "deps-consolidate",
                Title = "Use one version of each package",
                Why = "Different versions of the same package across projects cause binding redirects and restore conflicts once projects start to multi-target. This picks one version per package that satisfies every project, respects your pins, and checks it with a real restore.",
                Command = ["deps", "consolidate", "--all"],
                Writes = GuideWrites.Repository,
                Applies = (_, model) => model.Packages.Values.Any(u => u.Versions.Count > 1),
            },
            new GuideStep
            {
                Id = "resolve-dlls",
                Title = "Replace references to loose DLLs",
                Why = "References to DLLs checked into the repository hide what you depend on, and those DLLs rarely work on {target}. This replaces each with the project that builds it or the NuGet package that ships it, and lists the ones it cannot place.",
                Command = ["deps", "resolve-dlls"],
                Writes = GuideWrites.Repository,
                Applies = (_, model) => model.Projects.Any(p => p.AssemblyReferences.Any(r => r.Kind == AssemblyReferenceKind.File)),
            },
            new GuideStep
            {
                Id = "codemods",
                Title = "Apply the automatic rewrites",
                Why = "Codemods rewrite recurring patterns into code that works on both .NET Framework and {target}, such as System.Data.SqlClient to Microsoft.Data.SqlClient. You see every change as a diff first, and applied changes are checked with a build.",
                Command = ["codemod", "run", "--mod", "all"],
                Writes = GuideWrites.Repository,
            },
        ]),
        new("port", "Port, a wave at a time",
        [
            new GuideStep
            {
                Id = "port",
                Title = "Add {target} to the projects that are ready",
                Why = "A project is ready when everything it depends on already runs on {target}. This step adds {target} next to its .NET Framework target, and the dry run first builds both in a scratch copy. If that build fails, its errors are the to-do list: fix them in the .NET Framework code, so the repository keeps building, and run the step again. `offramp audit api --project P` explains each API, `offramp codemod run --project P` rewrites common patterns, #if NETFRAMEWORK covers the rest, and `offramp seams --project P` fences off code that cannot move. (To add the target anyway and fix forward on a branch, run the command with --accept-diff.) After applying, scan again: the project leaves the frontier and the next wave opens.",
                Command = ["csproj", "modernize", "--project", "{project}", "--tfm", "{tfms}"],
                Writes = GuideWrites.Repository,
                Observed = facts => facts.Model is { } model && !model.Projects.Any(p => p.FrameworkClass == FrameworkClass.Framework && !IsHostedApplication(p)),
                Projects = (facts, model) => model.Projects.Where(p => facts.IsReady(p) && !IsHostedApplication(p)),
                BlockedWithoutProjects = (_, model) =>
                {
                    var left = model.Projects.Count(p => p.FrameworkClass == FrameworkClass.Framework && !IsHostedApplication(p));
                    return $"{left} .NET Framework project(s) remain but none is ready: each depends on a web or service project or sits in a reference cycle. `offramp plan` shows what blocks each one.";
                },
            },
            new GuideStep
            {
                Id = "config-convert",
                Title = "Move application settings to appsettings.json",
                Why = "Modern .NET reads settings through IConfiguration, not App.config or Web.config. This turns appSettings, connection strings, and custom sections into appsettings.json with an options class per section, and can add a shim so existing ConfigurationManager calls keep working meanwhile.",
                Command = ["config", "convert", "--project", "{project}"],
                Writes = GuideWrites.Repository,
                Projects = (facts, model) => model.Projects.Where(p =>
                    Applications.Contains(p.Kind) && p.FrameworkClass == FrameworkClass.Framework && facts.ProjectsWithConfigFile.Contains(p.Id)),
            },
            new GuideStep
            {
                Id = "service",
                Title = "Turn Windows services into workers",
                Why = "A ServiceBase or Topshelf service becomes a worker for the generic host that runs in a Linux container, as a Windows service, or both, with health checks and JSON logs if you want them. The old project stays until you remove it.",
                Command = ["service", "--project", "{project}"],
                Writes = GuideWrites.Repository,
                Projects = (facts, model) => model.Projects.Where(p => p.Kind == ProjectKind.Service && facts.IsReady(p)),
            },
            new GuideStep
            {
                Id = "web-scaffold",
                Title = "Start an ASP.NET Core front for each web application",
                Why = "The strangler fig: a new ASP.NET Core application serves the controller actions that already port and forwards everything else to the old application, so the web front moves one route at a time while both run.",
                Command = ["web", "scaffold", "--project", "{project}", "--new", "{new}"],
                Writes = GuideWrites.Repository,
                Requires = ["web-inventory"],
                Projects = (_, model) => model.Projects.Where(p => p.Kind == ProjectKind.Web && p.FrameworkClass == FrameworkClass.Framework),
            },
        ]),
    ];

    public static IReadOnlyList<GuideStep> Steps { get; } = [.. Stages.SelectMany(s => s.Steps)];

    public static IReadOnlyList<string> Ids { get; } = [.. Steps.Select(s => s.Id)];

    public static GuideStep? Find(string id) => Steps.FirstOrDefault(s => s.Id == id);

    public static GuideStage StageOf(GuideStep step) => Stages.Single(s => s.Steps.Contains(step));

    /// <summary>The step's arguments after <c>offramp</c>, filled in for <paramref name="project"/>.</summary>
    public static IReadOnlyList<string> Arguments(GuideStep step, ProjectInfo? project, int target) =>
        project is null
            ? step.Command
            : [.. step.Command.Select(a => a
                .Replace("{project}", project.Id, StringComparison.Ordinal)
                .Replace("{tfms}", TargetFrameworks(project, target), StringComparison.Ordinal)
                .Replace("{new}", NewFolder(project), StringComparison.Ordinal))];

    /// <summary>The command as someone would type it, quoting arguments a shell would split.</summary>
    public static string Display(IEnumerable<string> arguments) =>
        "offramp " + string.Join(' ', arguments.Select(Quote));

    /// <summary>A text with <c>{target}</c> replaced by the target framework.</summary>
    public static string ForTarget(string text, int target) => text.Replace("{target}", $"net{target}.0", StringComparison.Ordinal);

    /// <summary>The project's target frameworks followed by the modern target (<c>-windows</c> for desktop projects).</summary>
    public static string TargetFrameworks(ProjectInfo project, int target)
    {
        var modern = project.Kind is ProjectKind.Winforms or ProjectKind.Wpf ? $"net{target}.0-windows" : $"net{target}.0";
        return string.Join(';', project.TargetFrameworks.Append(modern).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>A folder beside the project's folder, named after the project with <c>.Core</c> appended.</summary>
    public static string NewFolder(ProjectInfo project)
    {
        var projectFolder = Path.GetDirectoryName(project.Id)?.Replace('\\', '/') ?? "";
        var parent = Path.GetDirectoryName(projectFolder)?.Replace('\\', '/') ?? "";
        return parent.Length == 0 ? project.Name + ".Core" : $"{parent}/{project.Name}.Core";
    }

    /// <summary>Web and service projects move through their own steps (a new host), not by adding a target.</summary>
    private static bool IsHostedApplication(ProjectInfo project) => project.Kind is ProjectKind.Web or ProjectKind.Service;

    private static bool ReferencesTestFramework(ProjectInfo project) =>
        project.PackageReferences.Any(r => ProjectKindDetector.TestPackages.Contains(r.Id, StringComparer.OrdinalIgnoreCase))
        || project.AssemblyReferences.Any(r => TestAssemblies.Contains(r.Name, StringComparer.OrdinalIgnoreCase));

    private static string? ScanNote(GuideFacts facts, GuideRecord? record)
    {
        if (facts.Model is not null && facts.Staleness is { IsStale: true } staleness)
        {
            return $"The model is out of date: {staleness.Describe()}.";
        }

        return facts.Model is not null && record is { ExitCode: 1 }
            ? "The last scan's build failed, so the model covers only the projects that compiled. On macOS or Linux, see docs/compiling-on-macos.md."
            : null;
    }

    private static string Quote(string argument) =>
        argument.Length > 0 && argument.IndexOfAny([' ', ';', '"', '*', '&', '|', '<', '>', '(', ')', '\'']) < 0
            ? argument
            : "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}

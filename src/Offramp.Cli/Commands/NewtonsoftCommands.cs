using System.CommandLine;
using NuGet.Frameworks;
using NuGet.Versioning;
using Offramp.Analysis.Newtonsoft;
using Offramp.Cli.Infrastructure;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.NuGet.Feeds;
using Offramp.NuGet.Inspection;

namespace Offramp.Cli.Commands;

/// <summary>The <c>newtonsoft</c> command group (docs/spec/commands/newtonsoft.md).</summary>
public static class NewtonsoftCommands
{
    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var newtonsoft = new Command("newtonsoft", "Upgrade Newtonsoft.Json to 13.x: find what serializes, record what it does today, upgrade, and compare.");
        newtonsoft.Subcommands.Add(NewtonsoftScanCommand.Create(host, globals));
        newtonsoft.Subcommands.Add(NewtonsoftTestsCommand.Create(host, globals));
        return newtonsoft;
    }

    /// <summary>
    /// The target release and its package: <paramref name="version"/>, else the newest stable release in
    /// <c>newtonsoft.target</c> that supports every target framework of the projects that use
    /// Newtonsoft.Json. With no feed to ask, the newest measured release (OFR3725 says so).
    /// Null, with OFR3760, when there is none.
    /// </summary>
    internal static async Task<ChosenRelease?> TargetAsync(CommandContext context, WorkspaceModel model, string? version, CancellationToken cancellationToken)
    {
        var config = context.Config.Config;
        using var feeds = NuGetPackageFeeds.ForRepository(context.Repository.Path, config.Deps.Feeds);
        if (version is not null)
        {
            if (!NuGetVersion.TryParse(version, out var exact))
            {
                context.Diagnostics.Report(DiagnosticCatalog.OFR3760, $"--version '{version}' is not a version.");
                return null;
            }

            var chosen = await ReleaseChooser.ExactAsync(feeds, NewtonsoftReferences.PackageId, exact, cancellationToken);
            return chosen with { Problem = null };
        }

        if (!VersionRange.TryParse(config.Newtonsoft.Target, out var range))
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR3760, $"newtonsoft.target '{config.Newtonsoft.Target}' is not a NuGet version range.");
            return null;
        }

        var frameworks = model.Projects
            .Where(p => p.TargetFrameworks.Any(t => NewtonsoftReferences.For(p, t) is not null))
            .SelectMany(p => p.TargetFrameworks)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .Select(NuGetFramework.Parse)
            .ToList();
        var release = await ReleaseChooser.NewestSupportingAsync(feeds, NewtonsoftReferences.PackageId, range, frameworks, cancellationToken);
        if (release.Version is not null)
        {
            return release;
        }

        if (release.UnreachableSources.Count > 0 && range.Satisfies(NewtonsoftRules.Measured))
        {
            // Offline: judge against the newest release the rule pack measures; the trial compilation needs its package.
            return await ReleaseChooser.ExactAsync(feeds, NewtonsoftReferences.PackageId, NewtonsoftRules.Measured, cancellationToken) with { Problem = null };
        }

        context.Diagnostics.Report(DiagnosticCatalog.OFR3760, $"There is {release.Problem}.");
        return null;
    }
}

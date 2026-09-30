using System.Text.Json.Nodes;
using Offramp.Analysis.Compilations;
using Offramp.Analysis.Usage;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Core.Progress;
using Offramp.Analysis.Rules;

namespace Offramp.NuGet.Gac;

/// <summary>
/// <c>offramp deps gac</c>: .NET Framework assembly references and their modern
/// equivalents (rules/framework-assemblies.yml), with how much each is used, so an
/// unused reference is told apart from a real dependency.
/// </summary>
public static class GacAnalyzer
{
    public static GacResult Run(
        WorkspaceModel model, OfframpConfig config, string repositoryRoot, string? project, IProgressSink progress,
        DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        var projects = model.Projects
            .Where(p => project is null || string.Equals(p.Id, project, StringComparison.OrdinalIgnoreCase))
            .Where(p => p.AssemblyReferences.Any(r => r.Kind == AssemblyReferenceKind.Framework))
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .ToList();

        var results = new List<GacProject>();
        using var loader = new CompilationLoader(repositoryRoot);
        using (var phase = progress.BeginPhase("Counting framework assembly usage", 1, 1))
        {
            for (var i = 0; i < projects.Count; i++)
            {
                phase.Report(i, projects.Count, projects[i].Id);
                results.Add(Analyze(projects[i], loader, diagnostics, cancellationToken));
            }

            phase.Report(projects.Count, projects.Count);
        }

        var references = results.SelectMany(p => p.References).ToList();
        return new GacResult
        {
            Target = config.TargetFramework,
            Projects = results,
            Summary = new GacSummary(
                references.Count(r => r.Mapping.Kind == FrameworkAssemblyKind.Builtin),
                references.Count(r => r.Mapping.Kind == FrameworkAssemblyKind.Package),
                references.Count(r => r.Mapping.Kind == FrameworkAssemblyKind.CompatPack),
                references.Count(r => r.Mapping.Kind == FrameworkAssemblyKind.None),
                references.Count(r => r.Mapping.Kind == FrameworkAssemblyKind.Unknown),
                references.Count(r => r.Usages == 0)),
        };
    }

    private static readonly HashSet<string> SoapClientTypes = new(StringComparer.Ordinal)
    {
        "System.Web.Services.Protocols.SoapHttpClientProtocol", "System.Web.Services.Protocols.HttpWebClientProtocol",
        "System.Web.Services.Protocols.WebClientProtocol", "System.Web.Services.Protocols.SoapDocumentMethodAttribute",
        "System.Web.Services.Protocols.SoapRpcMethodAttribute", "System.Web.Services.Protocols.SoapHeader",
        "System.Web.Services.Protocols.SoapHeaderAttribute", "System.Web.Services.Protocols.SoapHeaderDirection",
        "System.Web.Services.Protocols.SoapException", "System.Web.Services.Protocols.InvokeCompletedEventArgs",
        "System.Web.Services.Description.SoapBindingUse", "System.Web.Services.Protocols.SoapParameterStyle",
        "System.Web.Services.WebServiceBindingAttribute", "System.Web.Services.WsiProfiles",
    };

    /// <summary>
    /// The table's mapping, made specific by what the project uses from the assembly (Open Live
    /// Writer): System.Web used only for <c>HttpUtility</c>, which modern .NET has, is not a move
    /// to ASP.NET Core, and System.Web.Services used only as a SOAP client moves to a WCF client.
    /// </summary>
    internal static FrameworkAssemblyMapping Refine(string assembly, FrameworkAssemblyMapping mapping, IReadOnlySet<string>? used)
    {
        if (used is not { Count: > 0 })
        {
            return mapping;
        }

        if (string.Equals(assembly, "System.Web", StringComparison.OrdinalIgnoreCase))
        {
            if (used.All(t => t == "System.Web.HttpUtility"))
            {
                return new FrameworkAssemblyMapping(FrameworkAssemblyKind.Builtin, null, false,
                    "Only HttpUtility is used, and modern .NET has it (System.Web.HttpUtility): remove the reference.");
            }

            if (used.All(t => t is "System.Web.HttpUtility" or "System.Web.MimeMapping"))
            {
                return mapping with
                {
                    Note = "Only HttpUtility and MimeMapping are used: modern .NET has HttpUtility (System.Web.HttpUtility); MimeMapping has no counterpart outside ASP.NET Core (FileExtensionContentTypeProvider), so keep a table of MIME types.",
                };
            }
        }

        if (string.Equals(assembly, "System.Web.Services", StringComparison.OrdinalIgnoreCase) && used.All(SoapClientTypes.Contains)
            && used.Any(t => t.EndsWith("ClientProtocol", StringComparison.Ordinal)))
        {
            return new FrameworkAssemblyMapping(FrameworkAssemblyKind.Package, "System.ServiceModel.Http", false,
                "Used as a SOAP client only: generate a WCF client with dotnet-svcutil (System.ServiceModel.Http and System.ServiceModel.Primitives) instead of the web reference.");
        }

        return mapping;
    }

    private static GacProject Analyze(ProjectInfo project, CompilationLoader loader, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        var names = project.AssemblyReferences.Where(r => r.Kind == AssemblyReferenceKind.Framework).Select(r => r.Name).ToList();
        IReadOnlyDictionary<string, int>? usages = null;
        IReadOnlyDictionary<string, IReadOnlySet<string>>? types = null;
        try
        {
            var compilation = loader.LoadForProject(project);
            if (compilation is not null && AssemblyUsage.Measure(compilation, names, cancellationToken) is { } measured)
            {
                (usages, types) = measured;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException)
        {
            diagnostics.Report(DiagnosticCatalog.OFR0132, $"The compilation of {project.Id} could not be rebuilt from the compiler log: {ex.Message}",
                new DiagnosticLocation(Project: project.Id),
                [KeyValuePair.Create<string, JsonNode?>("reason", ex.Message)]);
        }

        return new GacProject
        {
            Project = project.Id,
            TargetFrameworks = project.TargetFrameworks,
            References = [.. names
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(n => new GacReference
                {
                    Name = n,
                    Mapping = Refine(n, FrameworkAssemblyMap.Find(n), types?.GetValueOrDefault(n)),
                    Usages = usages is not null && usages.TryGetValue(n, out var count) ? count : null,
                })],
        };
    }
}

using NuGet.Frameworks;
using Offramp.Core.Model;

namespace Offramp.Workspace.Ingest;

/// <summary>Target framework helpers; every comparison goes through NuGet.Frameworks, never strings.</summary>
public static class Tfm
{
    /// <summary>
    /// Normalizes a short or long framework name ("net48", ".NETFramework,Version=v4.8",
    /// "net10.0-windows") to its short folder name, or null when it cannot be parsed.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var framework = NuGetFramework.Parse(value.Trim());
        return framework.IsUnsupported ? null : framework.GetShortFolderName();
    }

    /// <summary>The framework of a legacy project from TargetFrameworkIdentifier/Version.</summary>
    public static string? FromIdentifier(string? identifier, string? version)
    {
        if (string.IsNullOrWhiteSpace(identifier) || string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        return Normalize($"{identifier},Version={version}");
    }

    /// <summary>Orders frameworks: .NET Framework, then .NET Standard, then modern, each by version.</summary>
    public static IReadOnlyList<string> Sort(IEnumerable<string> tfms) =>
        [.. tfms.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(t => (Tfm: t, Framework: NuGetFramework.Parse(t)))
            .OrderBy(t => Rank(t.Framework))
            .ThenBy(t => t.Framework.Version)
            .ThenBy(t => t.Tfm, StringComparer.Ordinal)
            .Select(t => t.Tfm)];

    /// <summary>
    /// The framework class of a set of targets (docs/spec/00-architecture.md):
    /// net4x only = framework; netstandard only = standard; netN.0 only = modern;
    /// net4x plus anything else = dual. netstandard plus modern is standard
    /// (portable to every modern target); anything unrecognized is framework.
    /// </summary>
    public static FrameworkClass Classify(IEnumerable<string> tfms)
    {
        var kinds = tfms.Select(t => Rank(NuGetFramework.Parse(t))).ToHashSet();
        if (kinds.Count == 0)
        {
            return FrameworkClass.Framework;
        }

        var hasFramework = kinds.Contains(0) || kinds.Contains(3);
        var hasStandard = kinds.Contains(1);
        var hasModern = kinds.Contains(2);
        if (hasFramework)
        {
            return hasStandard || hasModern ? FrameworkClass.Dual : FrameworkClass.Framework;
        }

        return hasStandard ? FrameworkClass.Standard : FrameworkClass.Modern;
    }

    public static bool IsNetFramework(string tfm) =>
        NuGetFramework.Parse(tfm).Framework == FrameworkConstants.FrameworkIdentifiers.Net;

    private static int Rank(NuGetFramework framework) => framework.Framework switch
    {
        FrameworkConstants.FrameworkIdentifiers.Net => 0,
        FrameworkConstants.FrameworkIdentifiers.NetStandard => 1,
        FrameworkConstants.FrameworkIdentifiers.NetCoreApp => 2,
        _ => 3,
    };
}

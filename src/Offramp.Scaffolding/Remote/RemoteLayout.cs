using Microsoft.CodeAnalysis;
using Offramp.Core.Paths;
using Offramp.Scaffolding.Text;

namespace Offramp.Scaffolding.Remote;

/// <summary>Names and places of everything <c>remote</c> generates for one interface.</summary>
internal sealed record RemoteLayout
{
    public required INamedTypeSymbol Contract { get; init; }

    public required INamedTypeSymbol Implementation { get; init; }

    /// <summary>The implementation's project file, repository-relative.</summary>
    public required string ProjectFile { get; init; }

    public required string ContractsDirectory { get; init; }

    public required string ClientDirectory { get; init; }

    public required string HostDirectory { get; init; }

    /// <summary><c>\n</c> or <c>\r\n</c>, as the interface's file has it.</summary>
    public required string NewLine { get; init; }

    /// <summary>The repository manages package versions centrally: generated projects opt out and pin their own.</summary>
    public bool CentralPackages { get; init; }

    public string InterfaceName => Contract.Name;

    public string InterfaceFullName => CSharpText.Type(Contract);

    public string ImplementationFullName => CSharpText.Type(Implementation);

    /// <summary><c>IDirectoryLookup</c> → <c>DirectoryLookup</c>: the prefix of generated type names.</summary>
    public string Stem => CSharpText.Stem(Contract.Name);

    public string ContractsName => DirectoryName(ContractsDirectory);

    public string ClientName => DirectoryName(ClientDirectory);

    public string HostName => DirectoryName(HostDirectory);

    public string ContractsNamespace => Namespace(ContractsName);

    public string ClientNamespace => Namespace(ClientName);

    public string HostNamespace => Namespace(HostName);

    public string ContractsProject => $"{ContractsDirectory}/{ContractsName}.csproj";

    public string ClientProject => $"{ClientDirectory}/{ClientName}.csproj";

    public string HostProject => $"{HostDirectory}/{HostName}.csproj";

    public string Routes => $"{ContractsNamespace}.{Stem}Routes";

    /// <summary>The route of a member: <c>IDirectoryLookup/FindUser</c> (relative, so it works under a base path).</summary>
    public string Route(BoundaryMember member) => $"{InterfaceName}/{member.Key}";

    public string Request(BoundaryMember member) => $"{ContractsNamespace}.{member.Key}Request";

    /// <summary>A path from one generated project's directory to a repository file, with backslashes as MSBuild writes them.</summary>
    public static string Relative(string fromDirectory, string toFile) =>
        Path.GetRelativePath("/r/" + fromDirectory, "/r/" + toFile).Replace('/', '\\');

    /// <summary>The default places: next to the implementation's project directory.</summary>
    public static (string Contracts, string Client, string Host) Defaults(string projectFile, string projectName)
    {
        var directory = RepoPaths.Normalize(Path.GetDirectoryName(projectFile) ?? "");
        var parent = directory.Contains('/', StringComparison.Ordinal) ? directory[..directory.LastIndexOf('/')] + "/" : "";
        return (parent + projectName + ".Remote.Contracts", parent + projectName + ".Remote", parent + projectName + ".Windows.Host");
    }

    private static string DirectoryName(string directory) => directory.TrimEnd('/')[(directory.TrimEnd('/').LastIndexOf('/') + 1)..];

    private static string Namespace(string name)
    {
        var parts = name.Split('.').Select(p => new string([.. p.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_')])).Select(p => p.Length == 0 || char.IsDigit(p[0]) ? "_" + p : p);
        return string.Join(".", parts.Select(CSharpText.Identifier));
    }
}

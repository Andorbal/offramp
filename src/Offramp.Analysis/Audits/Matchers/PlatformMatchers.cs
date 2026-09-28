using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Offramp.Core.Paths;

namespace Offramp.Analysis.Audits.Matchers;

/// <summary>
/// <c>OFR3122</c>: <c>string.GetHashCode()</c> outside a <c>GetHashCode</c> or <c>Equals</c>
/// member. Inside those it feeds a hash table for the life of the process, which is fine;
/// anywhere else the value tends to be stored, sent, or used to pick a shard, and it changes
/// per process on modern .NET.
/// </summary>
public sealed class StringHashCodeMatcher : IAuditMatcher
{
    public string Name => "string-hash-code";

    public IEnumerable<RawFinding> Run(AuditMatchContext context)
    {
        if (context.Rule("OFR3122") is not { } rule)
        {
            yield break;
        }

        foreach (var (call, method, _) in Calls.In(context))
        {
            if (method.Name != "GetHashCode" || method.Parameters.Length != 0 || method.ContainingType?.SpecialType != SpecialType.System_String)
            {
                continue;
            }

            var member = call.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
            var hashing = member is MethodDeclarationSyntax { Identifier.ValueText: "GetHashCode" or "Equals" };
            if (!hashing)
            {
                yield return Calls.Finding(rule, call, method, "string.GetHashCode() differs per process on modern .NET; its value must not be stored or sent.");
            }
        }
    }
}

/// <summary>
/// <c>OFR3123</c>: a WCF client (<c>ClientBase&lt;T&gt;</c>) or <c>ChannelFactory&lt;T&gt;</c>
/// created without a binding and address, so the endpoint comes from
/// <c>system.serviceModel</c> in the configuration file, which the client packages on
/// modern .NET do not read.
/// </summary>
public sealed class WcfClientConfigMatcher : IAuditMatcher
{
    private const string ClientBase = "System.ServiceModel.ClientBase<TChannel>";
    private const string ChannelFactory = "System.ServiceModel.ChannelFactory<TChannel>";

    public string Name => "wcf-client-config";

    public IEnumerable<RawFinding> Run(AuditMatchContext context)
    {
        if (context.Rule("OFR3123") is not { } rule)
        {
            yield break;
        }

        foreach (var (call, method, _) in Calls.In(context))
        {
            if (method.MethodKind != MethodKind.Constructor || call is not BaseObjectCreationExpressionSyntax)
            {
                continue;
            }

            var kind = Derives(method.ContainingType, ClientBase) ? "client" : Derives(method.ContainingType, ChannelFactory) ? "channel factory" : null;
            if (kind is null || !FromConfiguration(method))
            {
                continue;
            }

            var how = method.Parameters.Length == 0 ? "the default endpoint" : $"the endpoint named by '{method.Parameters[0].Name}'";
            yield return Calls.Finding(rule, call, method,
                $"{AuditEngine.Name(method.ContainingType)} is created with {how} from system.serviceModel in the configuration file; the WCF {kind} on modern .NET does not read it and throws at runtime.");
        }
    }

    /// <summary>Parameterless, or the first parameter names an endpoint configuration.</summary>
    private static bool FromConfiguration(IMethodSymbol constructor) =>
        constructor.Parameters.Length == 0
        || (constructor.Parameters[0].Type.SpecialType == SpecialType.System_String
            && constructor.Parameters[0].Name is "endpointConfigurationName" or "endpointName");

    private static bool Derives(INamedTypeSymbol? type, string metadataName)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.OriginalDefinition.ToDisplayString() == metadataName)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// <c>OFR3126</c>: <c>Process.Start</c> or <c>ProcessStartInfo</c> with a constant file name
/// that is a Windows program: a <c>.exe</c>, <c>.bat</c>, <c>.cmd</c>, <c>.ps1</c>, <c>.vbs</c>,
/// or <c>.msi</c>, a drive-letter path, or one of the well-known command names.
/// </summary>
public sealed class WindowsExecutableMatcher : IAuditMatcher
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".bat", ".cmd", ".ps1", ".vbs", ".msi", ".com" };

    private static readonly HashSet<string> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "cscript", "wscript", "regsvr32", "rundll32", "sc", "net", "netsh", "xcopy", "robocopy", "taskkill", "tasklist",
        "iisreset", "msiexec", "notepad", "explorer", "schtasks", "reg", "ipconfig", "certutil", "wmic", "icacls", "attrib", "mstsc",
    };

    public string Name => "windows-executable";

    public IEnumerable<RawFinding> Run(AuditMatchContext context)
    {
        if (context.Rule("OFR3126") is not { } rule)
        {
            yield break;
        }

        foreach (var (call, method, model) in Calls.In(context))
        {
            var start = method.Name == "Start" && Calls.Is(method.ContainingType, "System.Diagnostics.Process");
            var info = method.MethodKind == MethodKind.Constructor && Calls.Is(method.ContainingType, "System.Diagnostics.ProcessStartInfo");
            if ((!start && !info) || Calls.Arguments(call) is not [var first, ..] || first.Expression is null)
            {
                continue;
            }

            if (Calls.Constant(model, first.Expression) is { } file && Program(file) is { } program)
            {
                yield return Calls.Finding(rule, call, method, $"{AuditEngine.Name(method)} starts {program}, a Windows program.",
                    new SortedDictionary<string, string>(StringComparer.Ordinal) { ["file"] = file });
            }
        }

        foreach (var tree in context.Trees)
        {
            var model = context.Compilation.GetSemanticModel(tree);
            foreach (var assignment in tree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "FileName" } access
                    || AuditEngine.Bound(model, access) is not IPropertySymbol property
                    || !Calls.Is(property.ContainingType, "System.Diagnostics.ProcessStartInfo"))
                {
                    continue;
                }

                if (Calls.Constant(model, assignment.Right) is { } file && Program(file) is { } program)
                {
                    yield return new RawFinding(rule, access.Name.GetLocation(), AuditEngine.Name(property),
                        $"ProcessStartInfo.FileName is {program}, a Windows program.",
                        new SortedDictionary<string, string>(StringComparer.Ordinal) { ["file"] = file }) { Namespace = AuditEngine.NamespaceOf(property) };
                }
            }
        }
    }

    /// <summary>The program's name when the file names a Windows program, else null.</summary>
    internal static string? Program(string file)
    {
        var trimmed = file.Trim().Trim('"');
        if (trimmed.Length == 0)
        {
            return null;
        }

        var name = trimmed.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var extension = Path.GetExtension(name);
        var driveLetter = trimmed.Length >= 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':';
        if (Extensions.Contains(extension) || (extension.Length == 0 && Commands.Contains(name)) || driveLetter)
        {
            return name;
        }

        return null;
    }
}

/// <summary>
/// <c>OFR3129</c>: a string literal that names a file in the repository, relative to the project
/// folder or the repository root, with different casing than the file has. Windows opens it;
/// Linux does not. Only literals that resolve to an existing file (ignoring case) count, so a
/// literal that names nothing in the repository is never a finding.
/// </summary>
public sealed class FileNameCaseMatcher : IAuditMatcher
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "packages", "TestResults", "artifacts",
    };

    public string Name => "file-name-case";

    public IEnumerable<RawFinding> Run(AuditMatchContext context)
    {
        if (context.Rule("OFR3129") is not { } rule)
        {
            yield break;
        }

        var files = context.Run.RepositoryFiles ??= Index(context.RepositoryRoot);
        var projectDirectory = RepoPaths.ToRepositoryRelative(context.RepositoryRoot, Path.GetDirectoryName(RepoPaths.ToAbsolute(context.RepositoryRoot, context.Project.Id))!);
        foreach (var tree in context.Trees)
        {
            foreach (var literal in tree.GetRoot().DescendantNodes().OfType<LiteralExpressionSyntax>().Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)))
            {
                if (Candidate(literal.Token.ValueText) is not { } candidate)
                {
                    continue;
                }

                foreach (var relative in new[] { Combine(projectDirectory, candidate), Combine("", candidate) }.OfType<string>().Distinct(StringComparer.Ordinal))
                {
                    if (!files.TryGetValue(relative, out var actual))
                    {
                        continue;
                    }

                    if (!string.Equals(actual, relative, StringComparison.Ordinal))
                    {
                        yield return new RawFinding(rule, literal.GetLocation(), literal.Token.ValueText,
                            $"\"{literal.Token.ValueText}\" names {actual}, which differs in case; the file is not found on a case-sensitive file system.",
                            new SortedDictionary<string, string>(StringComparer.Ordinal) { ["literal"] = literal.Token.ValueText, ["file"] = actual });
                    }

                    break;
                }
            }
        }
    }

    /// <summary>A literal that could be a relative file path: no URL, wildcard, or absolute root, and a file extension on its last segment.</summary>
    internal static string? Candidate(string text)
    {
        if (text.Length is < 3 or > 260 || text.Contains("://", StringComparison.Ordinal) || text.IndexOfAny(['\n', '\r', '*', '?', '{', '}', '<', '>', '|', '"']) >= 0)
        {
            return null;
        }

        var path = text.Replace('\\', '/');
        if (path.StartsWith('/') || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':'))
        {
            return null;
        }

        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        var lastSlash = path.LastIndexOf('/');
        var fileName = path[(lastSlash + 1)..];
        var dot = fileName.LastIndexOf('.');
        if (dot <= 0 || dot == fileName.Length - 1 || fileName.Length - dot - 1 > 10 || fileName.Contains(' ', StringComparison.Ordinal))
        {
            return null;
        }

        return path;
    }

    /// <summary>The repository-relative path of <paramref name="relative"/> under <paramref name="directory"/>, with <c>..</c> resolved, or null when it leaves the repository.</summary>
    private static string? Combine(string directory, string relative)
    {
        var segments = new List<string>();
        foreach (var segment in (directory.Length == 0 ? relative : directory + "/" + relative).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    return null;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return string.Join('/', segments);
    }

    private static Dictionary<string, string> Index(string root)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> entries;
            IEnumerable<string> subdirectories;
            try
            {
                entries = Directory.EnumerateFiles(directory);
                subdirectories = Directory.EnumerateDirectories(directory);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in entries)
            {
                var relative = RepoPaths.ToRepositoryRelative(root, file);
                files.TryAdd(relative, relative);
            }

            foreach (var subdirectory in subdirectories)
            {
                var name = Path.GetFileName(subdirectory);
                if (!name.StartsWith('.') && !SkippedDirectories.Contains(name))
                {
                    pending.Push(subdirectory);
                }
            }
        }

        return files;
    }
}

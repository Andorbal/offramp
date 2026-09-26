using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Offramp.Analysis.Audits;

namespace Offramp.Scaffolding.Service;

/// <summary>What a <c>ServiceInstaller</c> (or a Topshelf configuration) says about a service.</summary>
internal sealed record InstallerSettings
{
    public string? ServiceName { get; init; }

    public string? DisplayName { get; init; }

    public string? Description { get; init; }

    public string? StartType { get; init; }

    public IReadOnlyList<string> DependsOn { get; init; } = [];
}

/// <summary>A Topshelf <c>x.Service&lt;T&gt;(...)</c> configuration.</summary>
internal sealed record TopshelfConfiguration
{
    public required INamedTypeSymbol Type { get; init; }

    public required InvocationExpressionSyntax Run { get; init; }

    public LambdaExpressionSyntax? Construct { get; init; }

    public LambdaExpressionSyntax? Started { get; init; }

    public LambdaExpressionSyntax? Stopped { get; init; }

    public LambdaExpressionSyntax? Paused { get; init; }

    public LambdaExpressionSyntax? Continued { get; init; }

    public InstallerSettings Settings { get; init; } = new();

    public string? Account { get; init; }
}

/// <summary>
/// Finds Windows services in a project's recorded compilation: <c>ServiceBase</c> subclasses
/// with their installers, and Topshelf <c>HostFactory.Run</c> configurations.
/// </summary>
internal static class ServiceDetector
{
    public const string ServiceBaseName = "System.ServiceProcess.ServiceBase";

    public static List<INamedTypeSymbol> ServiceBaseClasses(Compilation compilation) =>
        [.. Declared(compilation)
            .Where(t => t is { TypeKind: TypeKind.Class, IsAbstract: false } && DerivesFromServiceBase(t))
            .OrderBy(AuditEngine.Name, StringComparer.Ordinal)];

    public static bool DerivesFromServiceBase(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == ServiceBaseName)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A member of <c>ServiceBase</c> itself (not of the derived class).</summary>
    public static bool IsServiceBaseMember(ISymbol? symbol) =>
        symbol is not null and not ITypeSymbol && symbol.ContainingType?.ToDisplayString() == ServiceBaseName;

    /// <summary>Installer settings per service name, and the account of the process installer.</summary>
    public static (Dictionary<string, InstallerSettings> Services, string? Account, List<string> Files) Installers(Compilation compilation)
    {
        var services = new Dictionary<ISymbol, InstallerSettings>(SymbolEqualityComparer.Default);
        string? account = null;
        var files = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var tree in AuditEngine.Sources(compilation))
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var assignment in tree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (assignment.Left is not MemberAccessExpressionSyntax access || model.GetSymbolInfo(access).Symbol is not IPropertySymbol property)
                {
                    continue;
                }

                var owner = property.ContainingType.ToDisplayString();
                if (owner == "System.ServiceProcess.ServiceProcessInstaller" && property.Name == "Account")
                {
                    account = model.GetSymbolInfo(assignment.Right).Symbol?.Name ?? account;
                    files.Add(tree.FilePath);
                    continue;
                }

                if (owner != "System.ServiceProcess.ServiceInstaller" || model.GetSymbolInfo(access.Expression).Symbol is not { } receiver)
                {
                    continue;
                }

                files.Add(tree.FilePath);
                var settings = services.GetValueOrDefault(receiver) ?? new InstallerSettings();
                services[receiver] = property.Name switch
                {
                    "ServiceName" => settings with { ServiceName = Text(model, assignment.Right) },
                    "DisplayName" => settings with { DisplayName = Text(model, assignment.Right) },
                    "Description" => settings with { Description = Text(model, assignment.Right) },
                    "StartType" => settings with { StartType = model.GetSymbolInfo(assignment.Right).Symbol?.Name },
                    "DelayedAutoStart" when model.GetConstantValue(assignment.Right) is { HasValue: true, Value: true } => settings with { StartType = "AutomaticDelayed" },
                    "ServicesDependedOn" => settings with { DependsOn = Strings(model, assignment.Right) },
                    _ => settings,
                };
            }
        }

        return (services.Values.Where(s => s.ServiceName is not null).GroupBy(s => s.ServiceName!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal), account, [.. files]);
    }

    /// <summary>Constant string values assigned to <c>ServiceBase</c> properties inside a type (ServiceName, CanPauseAndContinue, ...).</summary>
    public static Dictionary<string, object?> BaseSettings(Compilation compilation, INamedTypeSymbol type)
    {
        var settings = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var part in Parts(type))
        {
            var model = compilation.GetSemanticModel(part.SyntaxTree);
            foreach (var assignment in part.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (model.GetSymbolInfo(assignment.Left).Symbol is IPropertySymbol property && IsServiceBaseMember(property))
                {
                    var value = model.GetConstantValue(assignment.Right);
                    settings[property.Name] = value.HasValue ? value.Value : null;
                }
            }
        }

        return settings;
    }

    public static List<TypeDeclarationSyntax> Parts(INamedTypeSymbol type) =>
        [.. type.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<TypeDeclarationSyntax>().OrderBy(p => p.SyntaxTree.FilePath, StringComparer.Ordinal).ThenBy(p => p.SpanStart)];

    /// <summary>Topshelf <c>HostFactory.Run</c> / <c>New</c> configurations with a <c>Service&lt;T&gt;</c>.</summary>
    public static List<TopshelfConfiguration> Topshelf(Compilation compilation)
    {
        var found = new List<TopshelfConfiguration>();
        foreach (var tree in AuditEngine.Sources(compilation))
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var run in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(run).Symbol is not IMethodSymbol { Name: "Run" or "New" } method || method.ContainingType.ToDisplayString() != "Topshelf.HostFactory"
                    || run.ArgumentList.Arguments.FirstOrDefault()?.Expression is not LambdaExpressionSyntax configure)
                {
                    continue;
                }

                var calls = configure.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Select(i => (Invocation: i, Symbol: model.GetSymbolInfo(i).Symbol as IMethodSymbol))
                    .Where(c => c.Symbol?.ContainingNamespace?.ToDisplayString().StartsWith("Topshelf", StringComparison.Ordinal) == true)
                    .ToList();
                var settings = new InstallerSettings();
                string? account = null;
                var dependsOn = new List<string>();
                foreach (var (invocation, symbol) in calls)
                {
                    var argument = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                    switch (symbol!.Name)
                    {
                        case "SetServiceName":
                            settings = settings with { ServiceName = Text(model, argument) };
                            break;
                        case "SetDisplayName":
                            settings = settings with { DisplayName = Text(model, argument) };
                            break;
                        case "SetDescription":
                            settings = settings with { Description = Text(model, argument) };
                            break;
                        case "RunAsLocalSystem":
                            account = "LocalSystem";
                            break;
                        case "RunAsLocalService":
                            account = "LocalService";
                            break;
                        case "RunAsNetworkService":
                            account = "NetworkService";
                            break;
                        case "RunAs" or "RunAsPrompt" or "RunAsVirtualServiceAccount":
                            account = "User";
                            break;
                        case "StartAutomatically":
                            settings = settings with { StartType = "Automatic" };
                            break;
                        case "StartAutomaticallyDelayed":
                            settings = settings with { StartType = "AutomaticDelayed" };
                            break;
                        case "StartManually":
                            settings = settings with { StartType = "Manual" };
                            break;
                        case "Disabled":
                            settings = settings with { StartType = "Disabled" };
                            break;
                        case "DependsOn" when Text(model, argument) is { } dependency:
                            dependsOn.Add(dependency);
                            break;
                        case "DependsOnEventLog":
                            dependsOn.Add("EventLog");
                            break;
                        case "DependsOnMsSql":
                            dependsOn.Add("MSSQLSERVER");
                            break;
                    }
                }

                foreach (var (invocation, symbol) in calls.Where(c => c.Symbol!.Name == "Service" && c.Symbol.TypeArguments.Length == 1))
                {
                    if (symbol!.TypeArguments[0] is not INamedTypeSymbol type || invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression is not LambdaExpressionSyntax service)
                    {
                        continue;
                    }

                    LambdaExpressionSyntax? Lambda(string name) => service.DescendantNodes().OfType<InvocationExpressionSyntax>()
                        .Where(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: var n } && n == name)
                        .Select(i => i.ArgumentList.Arguments.FirstOrDefault()?.Expression as LambdaExpressionSyntax)
                        .FirstOrDefault(l => l is not null);
                    found.Add(new TopshelfConfiguration
                    {
                        Type = type,
                        Run = run,
                        Construct = Lambda("ConstructUsing"),
                        Started = Lambda("WhenStarted"),
                        Stopped = Lambda("WhenStopped"),
                        Paused = Lambda("WhenPaused"),
                        Continued = Lambda("WhenContinued"),
                        Settings = settings with { DependsOn = [.. dependsOn] },
                        Account = account,
                    });
                }
            }
        }

        return found;
    }

    /// <summary>How the code logs: EventLog, Trace, Debug, log4net, NLog, Serilog.</summary>
    public static List<string> Logging(Compilation compilation, IEnumerable<SyntaxNode> scope)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var node in scope)
        {
            var model = compilation.GetSemanticModel(node.SyntaxTree);
            foreach (var name in node.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var type = model.GetSymbolInfo(name).Symbol switch
                {
                    ITypeSymbol t => t,
                    { } s => s.ContainingType,
                    _ => null,
                };
                var display = type?.ToDisplayString() ?? "";
                var ns = type?.ContainingNamespace?.ToDisplayString() ?? "";
                var kind = display switch
                {
                    "System.Diagnostics.EventLog" => "EventLog",
                    "System.Diagnostics.Trace" => "Trace",
                    "System.Diagnostics.Debug" => "Debug",
                    _ when ns.StartsWith("log4net", StringComparison.Ordinal) => "log4net",
                    _ when ns.StartsWith("NLog", StringComparison.Ordinal) => "NLog",
                    _ when ns.StartsWith("Serilog", StringComparison.Ordinal) => "Serilog",
                    _ => null,
                };
                if (kind is not null)
                {
                    found.Add(kind);
                }
            }
        }

        return [.. found];
    }

    /// <summary>The keys read from <c>ConfigurationManager.AppSettings</c> (and connection strings, as <c>connectionStrings:NAME</c>).</summary>
    public static List<string> ConfigurationKeys(Compilation compilation, IEnumerable<SyntaxNode> scope)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var node in scope)
        {
            var model = compilation.GetSemanticModel(node.SyntaxTree);
            foreach (var access in node.DescendantNodes().OfType<ElementAccessExpressionSyntax>())
            {
                if (model.GetSymbolInfo(access.Expression).Symbol is not IPropertySymbol { ContainingType: var owner } property
                    || owner.ToDisplayString() != "System.Configuration.ConfigurationManager"
                    || access.ArgumentList.Arguments.FirstOrDefault()?.Expression is not { } argument
                    || Text(model, argument) is not { } key)
                {
                    continue;
                }

                keys.Add(property.Name == "ConnectionStrings" ? "connectionStrings:" + key : key);
            }
        }

        return [.. keys];
    }

    public static bool UsesConfigurationManager(Compilation compilation, IEnumerable<SyntaxNode> scope) =>
        scope.Any(node => node.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Any(n => n.Identifier.ValueText == "ConfigurationManager"
                && compilation.GetSemanticModel(node.SyntaxTree).GetSymbolInfo(n).Symbol is ITypeSymbol t && t.ToDisplayString() == "System.Configuration.ConfigurationManager"));

    private static IEnumerable<INamedTypeSymbol> Declared(Compilation compilation) =>
        AuditEngine.Sources(compilation)
            .SelectMany(t => t.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Select(c => compilation.GetSemanticModel(t).GetDeclaredSymbol(c)))
            .OfType<INamedTypeSymbol>()
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default);

    private static string? Text(SemanticModel model, ExpressionSyntax? expression) =>
        expression is not null && model.GetConstantValue(expression) is { HasValue: true, Value: string text } ? text : null;

    private static List<string> Strings(SemanticModel model, ExpressionSyntax expression)
    {
        var initializer = expression switch
        {
            ArrayCreationExpressionSyntax array => array.Initializer,
            ImplicitArrayCreationExpressionSyntax implicitArray => implicitArray.Initializer,
            InitializerExpressionSyntax direct => direct,
            _ => null,
        };
        return initializer is null ? [] : [.. initializer.Expressions.Select(e => Text(model, e)).OfType<string>()];
    }
}

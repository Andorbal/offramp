using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Offramp.Analysis.Audits;
using Offramp.Core.Diagnostics;
using DiagnosticDescriptor = Offramp.Core.Diagnostics.DiagnosticDescriptor;
using Offramp.Scaffolding.Text;

namespace Offramp.Scaffolding.Service;

/// <summary>A note about the conversion, reported as a diagnostic at the original code.</summary>
internal sealed record WorkerNote(DiagnosticDescriptor Descriptor, string Message, SyntaxNode? At);

/// <summary>A generated worker class and what went into it.</summary>
internal sealed record WorkerSource
{
    public required string Name { get; init; }

    public required string FullName { get; init; }

    public required string Text { get; init; }

    public required IReadOnlyList<ServiceTimer> Timers { get; init; }

    public required IReadOnlyList<string> Lifecycle { get; init; }

    /// <summary>The original code the worker keeps, for the linked-file closure and the usage scans.</summary>
    public required IReadOnlyList<SyntaxNode> Kept { get; init; }

    /// <summary>Project types the worker uses; their files are linked.</summary>
    public required IReadOnlyList<INamedTypeSymbol> Uses { get; init; }

    public required IReadOnlyList<WorkerNote> Notes { get; init; }
}

/// <summary>
/// Writes a <c>BackgroundService</c> for a service (docs/spec/commands/scaffold.md#service).
/// A <c>ServiceBase</c> class is lifted: its members keep their text; the lifecycle overrides
/// become private methods that <c>ExecuteAsync</c> and <c>StopAsync</c> call; a
/// <c>System.Timers.Timer</c> that only ticks becomes a <c>PeriodicTimer</c> loop that honors
/// the stopping token; <c>EventLog.WriteEntry</c> becomes logging; and statements that use
/// other <c>ServiceBase</c> members stay, excluded, in marked regions (OFR4102). A Topshelf
/// service is wrapped: the worker creates it and calls its start and stop code.
/// </summary>
internal static class WorkerWriter
{
    private const string Region = "OFFRAMP_SERVICEBASE";

    private static readonly HashSet<string> LifecycleNames = new(StringComparer.Ordinal)
    {
        "OnStart", "OnStop", "OnPause", "OnContinue", "OnShutdown", "OnCustomCommand", "OnSessionChange", "OnPowerEvent",
    };

    public static string WorkerName(INamedTypeSymbol type) =>
        (type.Name.EndsWith("Service", StringComparison.Ordinal) && type.Name.Length > "Service".Length ? type.Name[..^"Service".Length] : type.Name) + "Worker";

    public static WorkerSource FromServiceBase(Compilation compilation, INamedTypeSymbol type, string serviceName, string? heartbeat)
    {
        var name = WorkerName(type);
        var parts = ServiceDetector.Parts(type);
        var notes = new List<WorkerNote>();
        var edits = new Dictionary<SyntaxTree, TextEdits>();
        TextEdits Edits(SyntaxTree tree) => edits.TryGetValue(tree, out var e) ? e : edits[tree] = new TextEdits();

        var members = parts.SelectMany(p => p.Members).ToList();
        var dropped = new HashSet<SyntaxNode>();
        var regions = new Dictionary<SyntaxNode, string>();
        var initializeKept = DesignerBoilerplate(compilation, members, dropped, Edits);
        var lifecycle = new List<string>();
        foreach (var method in members.OfType<MethodDeclarationSyntax>().Where(m => m.Modifiers.Any(SyntaxKind.OverrideKeyword) && LifecycleNames.Contains(m.Identifier.ValueText)))
        {
            lifecycle.Add(method.Identifier.ValueText);
            if (method.Identifier.ValueText is "OnSessionChange" or "OnPowerEvent")
            {
                regions[method] = $"OFR4105: the host does not deliver {(method.Identifier.ValueText == "OnSessionChange" ? "session changes" : "power events")}";
                notes.Add(new WorkerNote(DiagnosticCatalog.OFR4105, $"{AuditEngine.Name(type)}.{method.Identifier.ValueText} handles {(method.Identifier.ValueText == "OnSessionChange" ? "session changes" : "power events")}, which the host does not deliver; it is kept in an excluded region of {name}.", method));
                continue;
            }

            var modifiers = method.Modifiers;
            var privateModifiers = string.Join(" ", modifiers.Where(m => m.IsKind(SyntaxKind.AsyncKeyword) || m.IsKind(SyntaxKind.UnsafeKeyword)).Select(m => m.Text).Prepend("private"));
            Edits(method.SyntaxTree).Replace(modifiers.First().SpanStart, modifiers.Last().Span.End, privateModifiers);
        }

        if (lifecycle.Contains("OnPause") || lifecycle.Contains("OnContinue") || lifecycle.Contains("OnCustomCommand"))
        {
            var what = string.Join(", ", lifecycle.Where(l => l is "OnPause" or "OnContinue" or "OnCustomCommand"));
            notes.Add(new WorkerNote(DiagnosticCatalog.OFR4101, $"{AuditEngine.Name(type)} handles {what}; the host has no pause, continue, or custom commands, so {name} keeps them as methods the application can call (Pause and Continue set Paused, which the timer loop honors).", members.OfType<MethodDeclarationSyntax>().First(m => m.Identifier.ValueText is "OnPause" or "OnContinue" or "OnCustomCommand")));
        }

        var timers = Timers(compilation, type, members, Edits, dropped);
        foreach (var member in members.Where(m => !dropped.Contains(m) && !regions.ContainsKey(m)))
        {
            RewriteServiceBaseUses(compilation, type, name, serviceName, member, initializeKept, Edits, regions, notes);
        }

        var kept = members.Where(m => !dropped.Contains(m)).ToList();
        var text = Assemble(compilation, type, name, parts, kept, regions, edits, timers, lifecycle, heartbeat);
        return new WorkerSource
        {
            Name = name,
            FullName = type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." + name : name,
            Text = text,
            Timers = [.. timers.Select(t => t.Timer)],
            Lifecycle = lifecycle,
            Kept = kept,
            Uses = Uses(compilation, type, kept),
            Notes = notes,
        };
    }

    public static WorkerSource FromTopshelf(Compilation compilation, TopshelfConfiguration configuration, string serviceName, string? heartbeat)
    {
        var type = configuration.Type;
        var name = WorkerName(type);
        var notes = new List<WorkerNote>();
        var model = compilation.GetSemanticModel(configuration.Run.SyntaxTree);
        var construct = configuration.Construct is { } c && Body(model, c, null, notes, serviceName) is { } created && !created.Contains(';', StringComparison.Ordinal)
            ? created
            : $"new {CSharpText.Type(type)}()";
        var start = configuration.Started is { } s ? Body(model, s, "_service", notes, serviceName) : null;
        var stop = configuration.Stopped is { } t ? Body(model, t, "_service", notes, serviceName) : null;
        if (configuration.Paused is not null || configuration.Continued is not null)
        {
            notes.Add(new WorkerNote(DiagnosticCatalog.OFR4101, $"{serviceName} handles pause and continue in its Topshelf configuration; the host has neither, so {name} does not call them.", configuration.Run));
        }

        var lifecycle = new List<string>();
        if (start is not null)
        {
            lifecycle.Add("WhenStarted");
        }

        if (stop is not null)
        {
            lifecycle.Add("WhenStopped");
        }

        var body = new StringBuilder();
        body.Append(CultureInfo.InvariantCulture, $$"""
                    private readonly {{CSharpText.Type(type)}} _service;

                    private readonly Microsoft.Extensions.Logging.ILogger<{{name}}> _workerLogger;
            {{(heartbeat is null ? "" : $"\n        private readonly {heartbeat} _workerHeartbeat;\n")}}
                    public {{name}}(Microsoft.Extensions.Logging.ILogger<{{name}}> logger{{(heartbeat is null ? "" : $", {heartbeat} heartbeat")}})
                    {
                        _workerLogger = logger;
            {{(heartbeat is null ? "" : "            _workerHeartbeat = heartbeat;\n")}}            _service = {{construct}};
                    }

                    protected override async System.Threading.Tasks.Task ExecuteAsync(System.Threading.CancellationToken stoppingToken)
                    {
                        await System.Threading.Tasks.Task.Yield();
            {{Indent(start, "            ")}}            Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(_workerLogger, "{Service} started.", "{{serviceName}}");
                        try
                        {
            {{KeepAlive(name, heartbeat)}}            }
                        catch (System.OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                        }
                    }

                    public override async System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken cancellationToken)
                    {
                        await base.StopAsync(cancellationToken);
            {{Indent(stop, "            ")}}        }

            """);
        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() : null;
        var text = Wrap(ns, [], $"/// <summary>Runs <c>{CSharpText.Type(type)}</c> (from its Topshelf configuration) as a hosted service.</summary>", name, body.ToString());
        return new WorkerSource
        {
            Name = name,
            FullName = ns is null ? name : ns + "." + name,
            Text = $"// Generated by offramp service from the Topshelf configuration of {CSharpText.Type(type)}; own this file.\n" + text,
            Timers = [],
            Lifecycle = lifecycle,
            Kept = [configuration.Run],
            Uses = [type],
            Notes = notes,
        };
    }

    // ----- ServiceBase: what goes -----

    /// <summary>
    /// Drops the designer's <c>components</c> field, its <c>Dispose(bool)</c>, and an
    /// <c>InitializeComponent</c> that only sets up components and ServiceBase properties.
    /// Returns whether <c>InitializeComponent</c> stays (with its other statements).
    /// </summary>
    private static bool DesignerBoilerplate(Compilation compilation, List<MemberDeclarationSyntax> members, HashSet<SyntaxNode> dropped, Func<SyntaxTree, TextEdits> edits)
    {
        foreach (var field in members.OfType<FieldDeclarationSyntax>().Where(f => f.Declaration.Variables.Count == 1 && f.Declaration.Variables[0].Identifier.ValueText == "components"))
        {
            dropped.Add(field);
        }

        foreach (var dispose in members.OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.ValueText == "Dispose" && m.Modifiers.Any(SyntaxKind.OverrideKeyword)))
        {
            var body = dispose.Body?.ToString() ?? "";
            if (body.Contains("components", StringComparison.Ordinal) && dispose.Body!.Statements.Count <= 2)
            {
                dropped.Add(dispose);
            }
        }

        var initialize = members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.ValueText == "InitializeComponent" && m.ParameterList.Parameters.Count == 0);
        if (initialize?.Body is null)
        {
            return false;
        }

        var model = compilation.GetSemanticModel(initialize.SyntaxTree);
        var others = initialize.Body.Statements.Where(s => !IsComponentsSetup(s) && !IsServiceBaseSetting(model, s)).ToList();
        if (others.Count > 0)
        {
            foreach (var statement in initialize.Body.Statements.Except(others))
            {
                edits(statement.SyntaxTree).Remove(statement.FullSpan.Start, statement.FullSpan.End);
            }

            return true;
        }

        dropped.Add(initialize);
        return false;

        static bool IsComponentsSetup(StatementSyntax statement) =>
            statement is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax { Left: var left } }
            && left.ToString() is "components" or "this.components";
    }

    private static bool IsServiceBaseSetting(SemanticModel model, StatementSyntax statement) =>
        statement is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment }
        && ServiceDetector.IsServiceBaseMember(model.GetSymbolInfo(assignment.Left).Symbol);

    // ----- ServiceBase: timers -----

    private sealed record ConvertedTimer(ServiceTimer Timer, string Tick);

    /// <summary>
    /// A <c>System.Timers.Timer</c> field whose every use is setup (interval, the Elapsed
    /// subscription, start, stop, dispose) becomes a <c>PeriodicTimer</c> loop: its setup
    /// statements and field go. Timers used any other way stay as they are (they work on
    /// modern .NET).
    /// </summary>
    private static List<ConvertedTimer> Timers(Compilation compilation, INamedTypeSymbol type, List<MemberDeclarationSyntax> members, Func<SyntaxTree, TextEdits> edits, HashSet<SyntaxNode> dropped)
    {
        var converted = new List<ConvertedTimer>();
        foreach (var field in members.OfType<FieldDeclarationSyntax>().Where(f => f.Declaration.Variables.Count == 1))
        {
            var model = compilation.GetSemanticModel(field.SyntaxTree);
            if (model.GetDeclaredSymbol(field.Declaration.Variables[0]) is not IFieldSymbol symbol || symbol.Type.ToDisplayString() != "System.Timers.Timer")
            {
                continue;
            }

            string? interval = Interval(model, field.Declaration.Variables[0].Initializer?.Value);
            string? tick = null;
            string? callback = null;
            var removals = new List<StatementSyntax>();
            var convertible = true;
            foreach (var use in members.SelectMany(m => m.DescendantNodes()).OfType<IdentifierNameSyntax>().Where(n => n.Identifier.ValueText == symbol.Name))
            {
                var useModel = compilation.GetSemanticModel(use.SyntaxTree);
                if (!SymbolEqualityComparer.Default.Equals(useModel.GetSymbolInfo(use).Symbol, symbol) || use.Ancestors().Contains(field))
                {
                    continue;
                }

                var statement = use.FirstAncestorOrSelf<StatementSyntax>();
                if (statement is not ExpressionStatementSyntax { Parent: BlockSyntax } expression)
                {
                    convertible = false;
                    break;
                }

                var member = use.Parent is MemberAccessExpressionSyntax access && access.Expression == use ? access.Name.Identifier.ValueText : null;
                switch (expression.Expression)
                {
                    case AssignmentExpressionSyntax { Left: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Interval" } left } assignment when left.Expression == use && Interval(useModel, assignment.Right) is { } value:
                        interval = value;
                        break;
                    case AssignmentExpressionSyntax { Left: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Enabled" or "AutoReset" } left } assignment when left.Expression == use:
                        if (left.Name.Identifier.ValueText == "AutoReset" && useModel.GetConstantValue(assignment.Right) is { HasValue: true, Value: false })
                        {
                            convertible = false;
                        }

                        break;
                    case AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.AddAssignmentExpression, Left: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Elapsed" } left } subscription when left.Expression == use && tick is null:
                        (tick, callback) = Tick(useModel, type, subscription.Right);
                        convertible &= tick is not null;
                        break;
                    case AssignmentExpressionSyntax { Left: var target, Right: ObjectCreationExpressionSyntax creation } when target == use:
                        interval = Interval(useModel, creation.ArgumentList?.Arguments.FirstOrDefault()?.Expression) ?? interval;
                        break;
                    case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax call } when call.Expression == use && member is "Start" or "Stop" or "Dispose" or "Close":
                        break;
                    default:
                        convertible = false;
                        break;
                }

                if (!convertible)
                {
                    break;
                }

                removals.Add(expression);
            }

            if (!convertible || tick is null || interval is null)
            {
                continue;
            }

            foreach (var statement in removals)
            {
                edits(statement.SyntaxTree).Remove(statement.FullSpan.Start, statement.FullSpan.End);
            }

            dropped.Add(field);
            converted.Add(new ConvertedTimer(new ServiceTimer { Name = symbol.Name, Kind = "System.Timers.Timer", Interval = interval, Callback = callback, Converted = true }, tick));
        }

        // Timers that stay, reported so the result lists what drives the service.
        foreach (var field in members.OfType<FieldDeclarationSyntax>())
        {
            var model = compilation.GetSemanticModel(field.SyntaxTree);
            foreach (var variable in field.Declaration.Variables)
            {
                if (model.GetDeclaredSymbol(variable) is IFieldSymbol { Type: var fieldType } symbol
                    && fieldType.ToDisplayString() is "System.Timers.Timer" or "System.Threading.Timer"
                    && converted.All(c => c.Timer.Name != symbol.Name))
                {
                    converted.Add(new ConvertedTimer(new ServiceTimer { Name = symbol.Name, Kind = fieldType.ToDisplayString(), Converted = false }, ""));
                }
            }
        }

        return converted;
    }

    /// <summary>An interval expression the loop can evaluate: no locals or parameters of the method that sets it.</summary>
    private static string? Interval(SemanticModel model, ExpressionSyntax? expression)
    {
        if (expression is null)
        {
            return null;
        }

        if (model.GetConstantValue(expression) is { HasValue: true, Value: var value } && value is not null)
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        return expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(n => model.GetSymbolInfo(n).Symbol is ILocalSymbol or IParameterSymbol) ? null : expression.ToString();
    }

    /// <summary>The tick action for an Elapsed handler that does not use its sender or arguments.</summary>
    private static (string? Tick, string? Callback) Tick(SemanticModel model, INamedTypeSymbol type, ExpressionSyntax handler)
    {
        if (handler is ObjectCreationExpressionSyntax { ArgumentList.Arguments: [var wrapped] })
        {
            handler = wrapped.Expression;
        }

        switch (handler)
        {
            case LambdaExpressionSyntax lambda:
                var parameters = lambda switch
                {
                    ParenthesizedLambdaExpressionSyntax p => p.ParameterList.Parameters.Select(x => x.Identifier.ValueText).ToList(),
                    SimpleLambdaExpressionSyntax s => [s.Parameter.Identifier.ValueText],
                    _ => [],
                };
                if (lambda.Body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(n => parameters.Contains(n.Identifier.ValueText)))
                {
                    return (null, null);
                }

                return ("() => " + lambda.Body.ToString(), null);
            default:
                if (model.GetSymbolInfo(handler).Symbol is not IMethodSymbol method || !SymbolEqualityComparer.Default.Equals(method.ContainingType, type)
                    || method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not MethodDeclarationSyntax declaration)
                {
                    return (null, null);
                }

                var names = declaration.ParameterList.Parameters.Select(p => p.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
                var body = (SyntaxNode?)declaration.Body ?? declaration.ExpressionBody;
                if (body is null || body.DescendantNodes().OfType<IdentifierNameSyntax>().Any(n => names.Contains(n.Identifier.ValueText)))
                {
                    return (null, null);
                }

                return ($"() => {method.Name}({string.Join(", ", method.Parameters.Select(_ => "null"))})", method.Name);
        }
    }

    // ----- ServiceBase: uses of the base class -----

    private static void RewriteServiceBaseUses(
        Compilation compilation, INamedTypeSymbol type, string worker, string serviceName, MemberDeclarationSyntax member, bool initializeKept,
        Func<SyntaxTree, TextEdits> edits, Dictionary<SyntaxNode, string> regions, List<WorkerNote> notes)
    {
        var model = compilation.GetSemanticModel(member.SyntaxTree);
        var edit = edits(member.SyntaxTree);
        var handled = new HashSet<SyntaxNode>();
        foreach (var name in member.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            var symbol = model.GetSymbolInfo(name).Symbol;
            if (symbol is INamedTypeSymbol named && SymbolEqualityComparer.Default.Equals(named, type))
            {
                edit.Replace(name.Identifier.SpanStart, name.Identifier.Span.End, worker);
                continue;
            }

            if (!initializeKept && symbol is IMethodSymbol { Name: "InitializeComponent" } && name.FirstAncestorOrSelf<StatementSyntax>() is ExpressionStatementSyntax call)
            {
                edit.Remove(call.FullSpan.Start, call.FullSpan.End);
                continue;
            }

            if (!ServiceDetector.IsServiceBaseMember(symbol))
            {
                continue;
            }

            var statement = name.FirstAncestorOrSelf<StatementSyntax>();
            if (statement is not null && handled.Contains(statement))
            {
                continue;
            }

            // EventLog.WriteEntry(message[, type, ...]) → logging.
            if (symbol is IPropertySymbol { Name: "EventLog" } && WriteEntry(model, name) is { } invocation)
            {
                var arguments = invocation.ArgumentList.Arguments;
                var level = arguments.Count > 1 && model.GetSymbolInfo(arguments[1].Expression).Symbol is IFieldSymbol entryType
                    ? entryType.Name switch { "Error" => "Error", "Warning" or "FailureAudit" => "Warning", _ => "Information" }
                    : "Information";
                edit.Replace(invocation.SpanStart, invocation.Span.End, $"Log(Microsoft.Extensions.Logging.LogLevel.{level}, {arguments[0].Expression})");
                continue;
            }

            // Settings (ServiceName = ..., CanStop = ...) and base calls go.
            if (statement is ExpressionStatementSyntax { Parent: BlockSyntax } expression
                && (IsServiceBaseSetting(model, expression) || expression.Expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax } }))
            {
                edit.Remove(expression.FullSpan.Start, expression.FullSpan.End);
                handled.Add(expression);
                continue;
            }

            // Reading ServiceName: the name is known.
            if (symbol is IPropertySymbol { Name: "ServiceName" } && !(name.Parent is AssignmentExpressionSyntax a && a.Left == name))
            {
                var node = name.Parent is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } qualified && qualified.Name == name ? (SyntaxNode)qualified : name;
                edit.Replace(node.SpanStart, node.Span.End, SymbolDisplay.FormatLiteral(serviceName, quote: true));
                continue;
            }

            if (statement is { Parent: BlockSyntax })
            {
                handled.Add(statement);
                Exclude(edit, statement, $"ServiceBase.{symbol!.Name} has no equivalent in the host");
                notes.Add(new WorkerNote(DiagnosticCatalog.OFR4102, $"{AuditEngine.Name(type)} uses ServiceBase.{symbol.Name}, which the host does not have; {worker} keeps the statement in an excluded region ({Region}) to review.", statement));
            }
            else if (regions.TryAdd(member, $"OFR4102: ServiceBase.{symbol!.Name} has no equivalent in the host"))
            {
                notes.Add(new WorkerNote(DiagnosticCatalog.OFR4102, $"{AuditEngine.Name(type)} uses ServiceBase.{symbol!.Name} outside a statement; {worker} keeps the whole member in an excluded region ({Region}) to review.", member));
            }
        }
    }

    private static InvocationExpressionSyntax? WriteEntry(SemanticModel model, SimpleNameSyntax name)
    {
        SyntaxNode receiver = name.Parent is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } qualified && qualified.Name == name ? qualified : name;
        return receiver.Parent is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "WriteEntry" } access && access.Expression == receiver
            && access.Parent is InvocationExpressionSyntax { ArgumentList.Arguments.Count: > 0 } invocation
            && model.GetSymbolInfo(invocation).Symbol is IMethodSymbol { IsStatic: false }
            ? invocation
            : null;
    }

    private static void Exclude(TextEdits edit, SyntaxNode node, string why)
    {
        edit.InsertOpening(node.FullSpan.Start, $"#if {Region} // OFR4102: {why}; review.\n");
        edit.InsertClosing(node.FullSpan.End, "#endif\n");
    }

    // ----- ServiceBase: the worker text -----

    private static string Assemble(
        Compilation compilation, INamedTypeSymbol type, string name, List<TypeDeclarationSyntax> parts, List<MemberDeclarationSyntax> kept, Dictionary<SyntaxNode, string> regions,
        Dictionary<SyntaxTree, TextEdits> edits, List<ConvertedTimer> timers, List<string> lifecycle, string? heartbeat)
    {
        var body = new StringBuilder();
        body.Append(CultureInfo.InvariantCulture, $"        private readonly Microsoft.Extensions.Logging.ILogger<{name}> _workerLogger;\n");
        if (heartbeat is not null)
        {
            body.Append(CultureInfo.InvariantCulture, $"\n        private readonly {heartbeat} _workerHeartbeat;\n");
        }

        var constructors = kept.OfType<ConstructorDeclarationSyntax>().Where(c => !c.Modifiers.Any(SyntaxKind.StaticKeyword)).ToList();
        var pausable = lifecycle.Contains("OnPause") || lifecycle.Contains("OnContinue");
        var converted = timers.Where(t => t.Timer.Converted).ToList();
        var injected = $"Microsoft.Extensions.Logging.ILogger<{name}> logger" + (heartbeat is null ? "" : $", {heartbeat} heartbeat");
        var assignments = "            _workerLogger = logger;\n" + (heartbeat is null ? "" : "            _workerHeartbeat = heartbeat;\n");
        foreach (var member in kept)
        {
            var text = MemberText(member, edits);
            if (member is ConstructorDeclarationSyntax constructor && constructors.Contains(constructor))
            {
                text = Constructor(constructor, text, name, injected, assignments);
            }

            text = WithoutBlankLines(text);
            if (regions.TryGetValue(member, out var reason))
            {
                text = $"#if {Region} // {reason}; review.\n{text}\n#endif";
            }

            body.Append('\n').Append(text).Append('\n');
        }

        if (constructors.Count == 0)
        {
            body.Append(CultureInfo.InvariantCulture, $"\n        public {name}({injected})\n        {{\n{assignments}        }}\n");
        }

        if (pausable)
        {
            body.Append("""

                        /// <summary>
                        /// The host has no pause or continue (OFR4101): nothing calls <see cref="Pause"/> or
                        /// <see cref="Continue"/> unless the application does. While paused, timer ticks are skipped.
                        /// </summary>
                        public bool Paused { get; private set; }

                        public void Pause()
                        {
                            Paused = true;

                """);
            body.Append(lifecycle.Contains("OnPause") ? "            OnPause();\n" : "").Append("        }\n\n        public void Continue()\n        {\n            Paused = false;\n");
            body.Append(lifecycle.Contains("OnContinue") ? "            OnContinue();\n" : "").Append("        }\n");
        }

        body.Append("""

                    protected override async System.Threading.Tasks.Task ExecuteAsync(System.Threading.CancellationToken stoppingToken)
                    {
                        // OnStart runs off the host's startup path, as it did under the service control manager.
                        await System.Threading.Tasks.Task.Yield();

            """);
        if (lifecycle.Contains("OnStart"))
        {
            body.Append("            OnStart(System.Array.Empty<string>());\n");
        }

        if (heartbeat is not null && converted.Count > 0)
        {
            body.Append(CultureInfo.InvariantCulture, $"            _workerHeartbeat.Beat(\"{name}\");\n");
        }

        body.Append("            try\n            {\n");
        if (converted.Count == 0)
        {
            body.Append(KeepAlive(name, heartbeat));
        }
        else
        {
            var loops = converted.Select(t => $"EveryAsync(System.TimeSpan.FromMilliseconds({t.Timer.Interval}), {t.Tick}, stoppingToken)").ToList();
            body.Append(loops.Count == 1
                ? $"                await {loops[0]};\n"
                : $"                await System.Threading.Tasks.Task.WhenAll(\n                    {string.Join(",\n                    ", loops)});\n");
        }

        body.Append("""
                        }
                        catch (System.OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                        }
                    }

            """);
        var stop = lifecycle.Contains("OnStop") ? "OnStop" : lifecycle.Contains("OnShutdown") ? "OnShutdown" : null;
        if (stop is not null)
        {
            body.Append(CultureInfo.InvariantCulture, $$"""

                        public override async System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken cancellationToken)
                        {
                            // Stop the ticks first, then run {{stop}}.
                            await base.StopAsync(cancellationToken);
                            {{stop}}();
                        }

                """);
        }

        if (converted.Count > 0)
        {
            body.Append(CultureInfo.InvariantCulture, $$"""

                        /// <summary>
                        /// A converted System.Timers.Timer: runs <paramref name="tick"/> every <paramref name="interval"/> until
                        /// the host stops. A failing tick is logged and the next one runs, as with the timer.
                        /// </summary>
                        private async System.Threading.Tasks.Task EveryAsync(System.TimeSpan interval, System.Action tick, System.Threading.CancellationToken stoppingToken)
                        {
                            using (var timer = new System.Threading.PeriodicTimer(interval))
                            {
                                while (await timer.WaitForNextTickAsync(stoppingToken))
                                {
                {{(pausable ? "                    if (Paused)\n                    {\n                        continue;\n                    }\n\n" : "")}}                    try
                                    {
                                        tick();
                                    }
                                    catch (System.Exception exception)
                                    {
                                        Microsoft.Extensions.Logging.LoggerExtensions.LogError(_workerLogger, exception, "A timer tick failed.");
                                    }
                {{(heartbeat is null ? "" : $"\n                    _workerHeartbeat.Beat(\"{name}\");\n")}}                }
                            }
                        }

                """);
        }

        var logs = body.ToString().Contains(" Log(Microsoft.Extensions.Logging.LogLevel.", StringComparison.Ordinal);
        if (logs)
        {
            body.Append("""

                    private void Log(Microsoft.Extensions.Logging.LogLevel level, string message)
                    {
                        Microsoft.Extensions.Logging.LoggerExtensions.Log(_workerLogger, level, "{Message}", message);
                    }

            """);
        }

        var usings = parts.Select(p => p.SyntaxTree.GetCompilationUnitRoot()).Distinct()
            .SelectMany(u => u.Usings.Concat(u.Members.OfType<BaseNamespaceDeclarationSyntax>().SelectMany(n => n.Usings)))
            .Select(u => u.ToString().Trim())
            .Where(u => !u.Contains("System.ServiceProcess", StringComparison.Ordinal) && !u.Contains("System.Configuration.Install", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(u => UsingName(u) is "System" || UsingName(u).StartsWith("System.", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(UsingName, StringComparer.Ordinal)
            .ToList();
        var doc = parts.Select(Documentation).FirstOrDefault(d => d.Length > 0);
        var summary = doc ?? $"/// <summary>{AuditEngine.Name(type)} as a hosted service.</summary>";
        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() : null;
        return Header(AuditEngine.Name(type)) + Wrap(ns, usings, summary, name, body.ToString());
    }

    private static string UsingName(string directive) => directive.Replace("using ", "", StringComparison.Ordinal).Replace("static ", "", StringComparison.Ordinal).TrimEnd(';').Trim();

    /// <summary>The class's documentation comment (recorded trees may hold it as plain <c>///</c> comments).</summary>
    private static string Documentation(TypeDeclarationSyntax part)
    {
        var lines = part.GetLeadingTrivia().ToFullString().Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("///", StringComparison.Ordinal))
            .ToList();
        return string.Join("\n", lines);
    }

    /// <summary>A member's text without the blank lines before it or after it.</summary>
    private static string WithoutBlankLines(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        while (lines.Count > 0 && lines[0].Trim().Length == 0)
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join("\n", lines);
    }

    private static string MemberText(MemberDeclarationSyntax member, Dictionary<SyntaxTree, TextEdits> edits)
    {
        var source = member.SyntaxTree.GetText().ToString();
        return edits.TryGetValue(member.SyntaxTree, out var edit)
            ? edit.Apply(source, member.FullSpan.Start, member.FullSpan.End)
            : source[member.FullSpan.Start..member.FullSpan.End];
    }

    /// <summary>The constructor renamed, with the logger (and heartbeat) injected first; <c>: base()</c> goes.</summary>
    private static string Constructor(ConstructorDeclarationSyntax constructor, string text, string name, string injected, string assignments)
    {
        var start = constructor.FullSpan.Start;
        var edits = new TextEdits();
        var identifier = constructor.Identifier.Span;
        edits.Replace(identifier.Start - start, identifier.End - start, name);
        var parameters = constructor.ParameterList;
        edits.InsertOpening(parameters.OpenParenToken.Span.End - start, injected + (parameters.Parameters.Count > 0 ? ", " : ""));
        if (constructor.Initializer is { ThisOrBaseKeyword.RawKind: (int)SyntaxKind.BaseKeyword } initializer)
        {
            edits.Remove(parameters.Span.End - start, initializer.Span.End - start);
        }

        if (constructor.Body is { } body)
        {
            edits.InsertOpening(body.OpenBraceToken.FullSpan.End - start, assignments);
        }

        return edits.Apply(text, 0, text.Length);
    }

    private static string KeepAlive(string name, string? heartbeat) => heartbeat is null
        ? "                await System.Threading.Tasks.Task.Delay(System.Threading.Timeout.Infinite, stoppingToken);\n"
        : $$"""
                            // Nothing ticks here, so the health check hears from the worker every ten seconds while it runs.
                            while (true)
                            {
                                _workerHeartbeat.Beat("{{name}}");
                                await System.Threading.Tasks.Task.Delay(System.TimeSpan.FromSeconds(10), stoppingToken);
                            }

            """;

    // ----- shared -----

    /// <summary>A Topshelf lambda's body with its parameter replaced by <paramref name="receiver"/>, as statements (or an expression for ConstructUsing).</summary>
    private static string? Body(SemanticModel model, LambdaExpressionSyntax lambda, string? receiver, List<WorkerNote> notes, string serviceName)
    {
        var parameters = lambda switch
        {
            ParenthesizedLambdaExpressionSyntax p => p.ParameterList.Parameters.ToList(),
            SimpleLambdaExpressionSyntax s => [s.Parameter],
            _ => [],
        };
        var first = parameters.Count > 0 ? model.GetDeclaredSymbol(parameters[0]) : null;
        var edits = new TextEdits();
        var start = lambda.Body.SpanStart;
        foreach (var name in lambda.Body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            var symbol = model.GetSymbolInfo(name).Symbol;
            if (receiver is not null && first is not null && SymbolEqualityComparer.Default.Equals(symbol, first))
            {
                edits.Replace(name.SpanStart - start, name.Span.End - start, receiver);
            }
            else if (symbol is IParameterSymbol parameter && parameters.Any(p => SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(p), parameter)))
            {
                notes.Add(new WorkerNote(DiagnosticCatalog.OFR4102, $"The Topshelf configuration of {serviceName} uses '{name.Identifier.ValueText}' (host settings or control), which the host does not have; review the generated worker.", lambda));
                return null;
            }
        }

        var text = edits.Apply(lambda.Body.ToString(), 0, lambda.Body.Span.Length);
        if (receiver is null)
        {
            return text;
        }

        return lambda.Body is BlockSyntax block
            ? string.Concat(block.Statements.Select(s => s.ToString()).Select(s => "            " + s.Replace("\n", "\n    ", StringComparison.Ordinal) + "\n"))
                .Replace("            " + receiver, "            " + receiver, StringComparison.Ordinal)
            : text + ";";
    }

    private static string Indent(string? statements, string indent) =>
        statements is null ? "" : statements.Contains('\n', StringComparison.Ordinal) ? statements : indent + statements + "\n";

    private static string Wrap(string? ns, List<string> usings, string summary, string name, string body)
    {
        var text = new StringBuilder();
        foreach (var directive in usings)
        {
            text.Append(directive).Append('\n');
        }

        if (usings.Count > 0)
        {
            text.Append('\n');
        }

        var indent = ns is null ? "" : "    ";
        if (ns is not null)
        {
            text.Append("namespace ").Append(ns).Append("\n{\n");
        }

        foreach (var line in summary.Split('\n'))
        {
            text.Append(indent).Append(line.TrimStart()).Append('\n');
        }

        text.Append(indent).Append("public sealed class ").Append(name).Append(" : Microsoft.Extensions.Hosting.BackgroundService\n").Append(indent).Append("{\n");
        text.Append(ns is null ? Outdent(body) : body);
        text.Append(indent).Append("}\n");
        if (ns is not null)
        {
            text.Append("}\n");
        }

        return text.ToString();
    }

    private static string Outdent(string body) =>
        string.Join("\n", body.Split('\n').Select(l => l.StartsWith("    ", StringComparison.Ordinal) ? l[4..] : l));

    private static string Header(string from) =>
        $"// Generated by offramp service from {from}. Review the regions marked OFR4102, then own this file.\n";

    /// <summary>Project types (declared in source) the kept members use, other than the service itself.</summary>
    private static List<INamedTypeSymbol> Uses(Compilation compilation, INamedTypeSymbol type, IEnumerable<SyntaxNode> kept)
    {
        var sources = AuditEngine.Sources(compilation).ToHashSet();
        var found = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var node in kept)
        {
            var model = compilation.GetSemanticModel(node.SyntaxTree);
            foreach (var name in node.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var symbol = model.GetSymbolInfo(name).Symbol;
                var used = symbol as INamedTypeSymbol ?? symbol?.ContainingType ?? model.GetTypeInfo(name).Type as INamedTypeSymbol;
                if (used?.OriginalDefinition is INamedTypeSymbol definition && !SymbolEqualityComparer.Default.Equals(definition, type)
                    && definition.DeclaringSyntaxReferences.Any(r => sources.Contains(r.SyntaxTree)))
                {
                    found.Add(definition);
                }
            }
        }

        return [.. found.OrderBy(AuditEngine.Name, StringComparer.Ordinal)];
    }
}

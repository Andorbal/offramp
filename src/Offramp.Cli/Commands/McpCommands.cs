using System.CommandLine;
using System.CommandLine.Completions;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Offramp.Cli.Infrastructure;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Progress;
using Offramp.Mcp;

namespace Offramp.Cli.Commands;

/// <summary><c>offramp mcp</c>: Offramp as a Model Context Protocol server (docs/spec/commands/mcp-and-llm.md#mcp-serve).</summary>
public static class McpCommands
{
    public static Command Create(CliHost host, GlobalOptions globals)
    {
        _ = globals;
        var mcp = new Command("mcp", "Offramp for AI agents: a Model Context Protocol server.");
        var allowApply = new Option<bool>("--allow-apply") { Description = "Honor --apply and --yes in tool calls; without it every call is a dry run." };
        var root = new Option<string?>("--root") { Description = "Confine every path a tool call names to this directory, and run the tools there (default: the current directory).", HelpName = "PATH" };
        var serve = new Command("serve", "Serve every command as an MCP tool (offramp_<group>_<command>) over stdio, with the workspace model, plans, the ledger, and diagnostic documentation as resources.")
        {
            allowApply, root,
        };
        serve.SetAction(async (parse, ct) =>
        {
            var directory = Path.GetFullPath(parse.GetValue(root) ?? ".", host.WorkingDirectory);
            if (!Directory.Exists(directory))
            {
                await host.Error.WriteLineAsync($"error: --root {directory} is not a directory.");
                return ExitCodes.Usage;
            }

            await OfframpMcpServer.RunAsync(McpToolCatalog.Build(host, directory, parse.GetValue(allowApply)), ct);
            return ExitCodes.Success;
        });
        HelpExamples.Add(serve, "offramp mcp serve", "offramp mcp serve --root ~/src/monolith --allow-apply");
        mcp.Subcommands.Add(serve);
        return mcp;
    }
}

/// <summary>
/// The tools and resources of <c>mcp serve</c>, built from the CLI's command tree: one tool per
/// command, its input schema from the command's options and arguments, and each call a run of
/// <see cref="OfframpCli.RunAsync"/> with <c>--json</c>, so the terminal and the agent get the
/// same handlers, the same envelope, and the same exit codes.
/// </summary>
public static class McpToolCatalog
{
    /// <summary>The global options a tool accepts; output shape (<c>--json</c>, <c>--quiet</c>) is the server's.</summary>
    private static readonly string[] ToolGlobals =
        ["--target", "--solution", "--config", "--out", "--dry-run", "--apply", "--yes", "--verbose", "--no-cache", "--llm", "--no-llm", "--fail-on", "--fail-on-stale"];

    private static readonly string[] ApplyOptions = ["--apply", "--yes"];

    public const string Workflow = """
        Suggested workflow: offramp_doctor (environment), offramp_scan (build the workspace model; everything else reads it),
        then look: offramp_graph, offramp_deps_audit, offramp_audit_api, offramp_plan (the order to port projects in);
        then change, one step at a time, each verified by a build: offramp_move_tests, offramp_move_plan with offramp_move_apply,
        offramp_move_extract, offramp_codemod_run, offramp_csproj_modernize; offramp_verify builds; offramp_report shows progress.
        Every tool returns the command's JSON envelope (result, diagnostics with OFR codes, exit code). Commands that write are
        dry runs unless the server allows --apply; offramp_move_rollback undoes an applied change from its journal.
        """;

    public static McpCatalog Build(CliHost host, string root, bool allowApply)
    {
        var tree = OfframpCli.BuildRoot(host);
        var globals = tree.Options.Where(o => ToolGlobals.Contains(o.Name)).ToList();
        var tools = Commands(tree, [])
            .Where(c => c.Path[0] != "mcp")
            .Select(c => Tool(host, root, allowApply, c.Path, c.Command, globals))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
        tools.Insert(0, Help(tools, allowApply));
        return new McpCatalog
        {
            ServerName = "offramp",
            ServerVersion = OfframpVersion.Current,
            Instructions = "Offramp migrates .NET Framework codebases to modern .NET: deterministic analysis, pure file moves, verified by real builds. Call offramp_help first.",
            Tools = tools,
            Resources = () => McpResources.List(host, root),
            ResourceTemplates = McpResources.Templates,
            ReadResourceAsync = (uri, ct) => McpResources.ReadAsync(host, root, uri, ct),
        };
    }

    /// <summary>The tool name of a command path: <c>offramp_deps_audit</c>.</summary>
    public static string Name(IEnumerable<string> path) => "offramp_" + string.Join('_', path).Replace('-', '_');

    private static IEnumerable<(string[] Path, Command Command)> Commands(Command command, string[] path)
    {
        foreach (var sub in command.Subcommands.Where(s => !s.Hidden))
        {
            string[] subPath = [.. path, sub.Name];
            if (sub.Action is not null)
            {
                yield return (subPath, sub);
            }

            foreach (var nested in Commands(sub, subPath))
            {
                yield return nested;
            }
        }
    }

    private static McpToolDefinition Help(IReadOnlyList<McpToolDefinition> tools, bool allowApply) => new(
        "offramp_help",
        "Lists Offramp's tools and the suggested migration workflow. Call this first.",
        new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
        (_, _) =>
        {
            var text = new StringBuilder();
            text.Append(Workflow.Trim()).Append("\n\n");
            text.Append(allowApply ? "This server honors --apply.\n\n" : "This server runs every tool as a dry run (started without --allow-apply).\n\n");
            foreach (var tool in tools)
            {
                text.Append(CultureInfo.InvariantCulture, $"- {tool.Name}: {tool.Description}\n");
            }

            return Task.FromResult(new McpToolOutput([text.ToString()], IsError: false));
        });

    private static McpToolDefinition Tool(CliHost host, string root, bool allowApply, string[] path, Command command, IReadOnlyList<Option> globals)
    {
        var options = command.Options.Where(o => o is not System.CommandLine.Help.HelpOption && !o.Hidden).Concat(globals).GroupBy(o => o.Name).Select(g => g.First()).ToList();
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var option in options)
        {
            properties[Key(option.Name)] = Schema(option.ValueType, option.Description, Values(option));
            if (option.Required)
            {
                required.Add(Key(option.Name));
            }
        }

        foreach (var argument in command.Arguments.Where(a => !a.Hidden))
        {
            properties[argument.Name] = Schema(argument.ValueType, argument.Description, []);
            if (argument.Arity.MinimumNumberOfValues > 0)
            {
                required.Add(argument.Name);
            }
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        var description = command.Description ?? string.Join(' ', path);
        if (!allowApply && options.Any(o => o.Name == "--apply"))
        {
            description += " (Dry run on this server: apply is ignored.)";
        }

        return new McpToolDefinition(Name(path), description, schema,
            (call, ct) => InvokeAsync(host, root, allowApply, path, options, [.. command.Arguments.Where(a => !a.Hidden)], call, ct));
    }

    /// <summary>The option's name as a JSON property: without the leading dashes.</summary>
    private static string Key(string option) => option.TrimStart('-');

    private static JsonObject Schema(Type type, string? description, IReadOnlyList<string> values)
    {
        var element = Nullable.GetUnderlyingType(type) ?? type;
        JsonObject schema;
        if (element == typeof(bool))
        {
            schema = new JsonObject { ["type"] = "boolean" };
        }
        else if (element == typeof(int) || element == typeof(long))
        {
            schema = new JsonObject { ["type"] = "integer" };
        }
        else if (element == typeof(double) || element == typeof(decimal))
        {
            schema = new JsonObject { ["type"] = "number" };
        }
        else if (element != typeof(string) && element.IsArray)
        {
            schema = new JsonObject { ["type"] = "array", ["items"] = Schema(element.GetElementType()!, null, values) };
            values = [];
        }
        else if (element.IsEnum)
        {
            schema = new JsonObject { ["type"] = "string" };
            values = [.. Enum.GetNames(element).Select(n => char.ToLowerInvariant(n[0]) + n[1..])];
        }
        else
        {
            schema = new JsonObject { ["type"] = "string" };
        }

        if (values.Count > 0)
        {
            schema["enum"] = new JsonArray([.. values.Select(v => (JsonNode)v)]);
        }

        if (description is not null)
        {
            schema["description"] = description;
        }

        return schema;
    }

    /// <summary>The values an option accepts (<c>AcceptOnlyFromAmong</c>), from its completions.</summary>
    private static IReadOnlyList<string> Values(Option option)
    {
        var element = Nullable.GetUnderlyingType(option.ValueType) ?? option.ValueType;
        if (element == typeof(bool) || element.IsEnum)
        {
            return [];
        }

        return [.. option.GetCompletions(CompletionContext.Empty).Select(c => c.Label).Where(l => !l.StartsWith('-')).Distinct(StringComparer.Ordinal)];
    }

    private static async Task<McpToolOutput> InvokeAsync(CliHost host, string root, bool allowApply, string[] path, IReadOnlyList<Option> options,
        IReadOnlyList<Argument> arguments, McpToolCall call, CancellationToken cancellationToken)
    {
        var args = new List<string>(path);
        var positional = new SortedDictionary<int, List<string>>();
        var ignored = new List<string>();
        foreach (var (key, value) in call.Arguments.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            var option = options.FirstOrDefault(o => Key(o.Name) == key);
            var argumentIndex = arguments.Select((a, i) => (a, i)).FirstOrDefault(a => a.a.Name == key);
            if (option is null && argumentIndex.a is null)
            {
                return Refused($"The tool has no parameter '{key}'.");
            }

            var values = Values(value);
            if (values.FirstOrDefault(v => !Inside(root, v)) is { } outside)
            {
                return Refused(DiagnosticCatalog.OFR9101, $"'{outside}' is outside the server's root {root}; the tool did not run.");
            }

            if (option is null)
            {
                positional[argumentIndex.i] = values;
                continue;
            }

            if (!allowApply && ApplyOptions.Contains(option.Name))
            {
                if (value.ValueKind == JsonValueKind.True)
                {
                    ignored.Add(option.Name);
                }

                continue;
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.True:
                    args.Add(option.Name);
                    break;
                case JsonValueKind.False or JsonValueKind.Null:
                    break;
                case JsonValueKind.Array when option.AllowMultipleArgumentsPerToken && values.Count > 0:
                    args.Add(option.Name);
                    args.AddRange(values);
                    break;
                default:
                    foreach (var item in values)
                    {
                        args.Add(option.Name);
                        args.Add(item);
                    }

                    break;
            }
        }

        args.AddRange(positional.Values.SelectMany(v => v));
        if (!allowApply)
        {
            args.Add("--dry-run");
        }

        args.Add("--json");
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var invocation = host with
        {
            Out = output,
            Error = error,
            OutputIsTerminal = false,
            ErrorIsTerminal = false,
            InputIsTerminal = false,
            WorkingDirectory = root,
            Progress = new McpProgressSink(call.Progress),
        };
        var exitCode = await OfframpCli.RunAsync([.. args], invocation, cancellationToken);
        var texts = new List<string>();
        var envelope = output.ToString();
        texts.Add(envelope.Length > 0 ? envelope : error.ToString());
        if (ignored.Count > 0)
        {
            texts.Add($"Dry run: this server was started without --allow-apply, so {string.Join(" and ", ignored)} {(ignored.Count == 1 ? "was" : "were")} ignored and nothing was written.");
        }

        return new McpToolOutput(texts, IsError: envelope.Length == 0 || exitCode is ExitCodes.Usage or ExitCodes.Environment);
    }

    /// <summary>A parameter's values as command-line tokens.</summary>
    private static List<string> Values(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => [.. value.EnumerateArray().SelectMany(Values)],
        JsonValueKind.String => [value.GetString()!],
        JsonValueKind.Number => [value.GetRawText()],
        JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => [],
        _ => [value.GetRawText()],
    };

    /// <summary>True unless the value is a path that leaves the root (absolute elsewhere, or climbing out with <c>..</c>). URLs are not paths.</summary>
    public static bool Inside(string root, string value)
    {
        if (value.Contains("://", StringComparison.Ordinal) || (!Path.IsPathRooted(value) && !value.Replace('\\', '/').Split('/').Contains("..")))
        {
            return true;
        }

        var full = Path.GetFullPath(value, root);
        var relative = Path.GetRelativePath(root, full);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static McpToolOutput Refused(string message) => new([message], IsError: true);

    private static McpToolOutput Refused(DiagnosticDescriptor descriptor, string message) =>
        new([new JsonObject { ["code"] = descriptor.Code, ["severity"] = "error", ["message"] = message, ["help"] = descriptor.HelpUri }.ToJsonString()], IsError: true);

    /// <summary>Progress events as MCP notifications: every event advances the count by one, with the phase and item as the message.</summary>
    private sealed class McpProgressSink(IProgress<McpProgress> progress) : IProgressSink
    {
        private readonly object _gate = new();
        private int _count;

        public IProgressPhase BeginPhase(string name, int index, int of)
        {
            Send(string.Create(CultureInfo.InvariantCulture, $"{name} ({index}/{of})"));
            return new Phase(this, name);
        }

        public void Log(ProgressLevel level, string message)
        {
            if (level >= ProgressLevel.Info)
            {
                Send(message);
            }
        }

        /// <summary>Numbers and sends under one lock: parallel work reports concurrently, and MCP progress must increase.</summary>
        private void Send(string message)
        {
            lock (_gate)
            {
                progress.Report(new McpProgress(++_count, null, message));
            }
        }

        private sealed class Phase(McpProgressSink sink, string name) : IProgressPhase
        {
            public string Name => name;

            public void Report(int current, int total, string? item = null) =>
                sink.Send(string.Create(CultureInfo.InvariantCulture, $"{name}: {current}/{total}{(item is null ? "" : " " + item)}"));

            public void Dispose()
            {
            }
        }
    }
}

/// <summary>The server's resources: the workspace model, the ledger, move plans, and diagnostic documentation.</summary>
public static class McpResources
{
    public static IReadOnlyList<McpResourceTemplateDefinition> Templates { get; } =
    [
        new("offramp://plan/{id}", "Move plan", "A plan under the state directory's plans/ folder (move plan --out, move extract).", "application/json"),
        new("offramp://diagnostics/{code}", "Diagnostic", "What an OFR code means, its common causes, and the fix.", "text/markdown"),
    ];

    public static IReadOnlyList<McpResourceDefinition> List(CliHost host, string root)
    {
        var state = State(host, root);
        var list = new List<McpResourceDefinition>
        {
            new("offramp://workspace", "Workspace model", "The workspace model offramp scan wrote (workspace.json).", "application/json"),
            new("offramp://ledger", "Ledger", "The ledger's snapshots, oldest first, and the latest one.", "application/json"),
        };
        var plans = Path.Combine(state, "plans");
        if (Directory.Exists(plans))
        {
            list.AddRange(Directory.EnumerateFiles(plans, "*.json").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal)
                .Select(id => new McpResourceDefinition($"offramp://plan/{id}", $"Plan {id}", "A move plan.", "application/json")));
        }

        return list;
    }

    public static Task<(string Text, string MimeType)?> ReadAsync(CliHost host, string root, string uri, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var state = State(host, root);
        (string, string)? Json(string path) => File.Exists(path) ? (File.ReadAllText(path), "application/json") : null;
        return Task.FromResult(uri switch
        {
            "offramp://workspace" => Json(Path.Combine(state, "workspace.json")),
            "offramp://ledger" => Ledger(state),
            _ when uri.StartsWith("offramp://plan/", StringComparison.Ordinal) && Id(uri["offramp://plan/".Length..]) is { } id => Json(Path.Combine(state, "plans", id + ".json")),
            _ when uri.StartsWith("offramp://diagnostics/", StringComparison.Ordinal) && DiagnosticCatalog.Find(uri["offramp://diagnostics/".Length..].ToUpperInvariant()) is { } d =>
                ($"# {d.Code}: {d.Title}\n\nSeverity: {d.DefaultSeverity.ToString().ToLowerInvariant()}. Area: {d.Area}.\n\n{d.Meaning}\n\n**Common causes.** {d.Cause}\n\n**Fix.** {d.Fix}\n\n{d.HelpUri}\n", "text/markdown"),
            _ => ((string, string)?)null,
        });
    }

    /// <summary>A plan id: a file name without separators or <c>..</c>.</summary>
    private static string? Id(string id) => id.Length > 0 && id.IndexOfAny(['/', '\\']) < 0 && id != ".." ? id : null;

    private static (string, string)? Ledger(string state)
    {
        var folder = Path.Combine(state, "ledger");
        var files = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal).ToList() : [];
        var result = new JsonObject
        {
            ["snapshots"] = new JsonArray([.. files.Select(f => (JsonNode)Path.GetFileName(f))]),
            ["latest"] = files.Count == 0 ? null : JsonNode.Parse(File.ReadAllText(files[^1])),
        };
        return (result.ToJsonString(), "application/json");
    }

    /// <summary>The state directory (<c>paths.state</c>, default <c>.offramp</c>) under the root.</summary>
    private static string State(CliHost host, string root)
    {
        var config = ConfigLoader.Load(new ConfigSources { RepositoryRoot = root, Environment = host.Environment });
        return Path.GetFullPath(config.Config.Paths.State, root);
    }
}

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Offramp.Core.Caching;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Git;
using Offramp.Core.Paths;
using Offramp.Core.Processes;
using Offramp.Core.Progress;
using Offramp.Refactoring.Moves;
using Offramp.Workspace.Store;
using Offramp.Workspace.Targets;

namespace Offramp.Ide.Lsp;

/// <summary>What the language server needs from its host (the CLI).</summary>
public sealed record IdeServerOptions
{
    public required IProcessRunner Processes { get; init; }

    public required IGitService Git { get; init; }

    /// <summary>The process environment (<c>OFFRAMP_*</c> configuration).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Runs <c>offramp scan</c> in a repository through the CLI's command tree; returns the exit code.</summary>
    public required Func<string, IProgressSink, CancellationToken, Task<int>> ScanAsync { get; init; }

    public string ServerVersion { get; init; } = "0.0.0";

    /// <summary>How long typing must pause before a document is analyzed again.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Where the server logs (stderr); never stdout, which carries the protocol.</summary>
    public TextWriter Log { get; init; } = TextWriter.Null;
}

/// <summary>
/// <c>offramp ide serve</c> (docs/spec/commands/ide.md#offramp-ide-serve): a Language Server
/// Protocol server for one repository. Diagnostics, lenses, and code actions are renderings of
/// the engine's file reports (<see cref="LspRender"/>); <c>offramp.move</c> plans, asks, applies,
/// and reports. Engine calls are serialized.
/// </summary>
public sealed class OfframpLanguageServer : IDisposable
{
    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;
    private const int ServerNotInitialized = -32002;
    private const int RequestCancelled = -32800;

    private readonly LspConnection _connection;
    private readonly IdeServerOptions _options;
    private readonly SemaphoreSlim _engineLock = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounce = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Uri, string Text)> _documents = new(StringComparer.Ordinal);
    private IdeEngine? _engine;
    private string? _root;
    private string? _canonicalRoot;
    private IdeSettings _settings = new();
    private bool _workDoneProgress;
    private bool _showDocument;
    private bool _codeLensRefresh;
    private bool _shutdown;
    private bool _scanAsked;
    private int _nextId;
    private string _model = "missing";
    private string? _message;
    private DateTimeOffset _baseCheckedAt;

    private OfframpLanguageServer(LspConnection connection, IdeServerOptions options)
    {
        _connection = connection;
        _options = options;
    }

    /// <summary>Serves until <c>exit</c> or the end of the input; 0 after an orderly shutdown.</summary>
    public static async Task<int> RunAsync(Stream input, Stream output, IdeServerOptions options, CancellationToken cancellationToken)
    {
        using var connection = new LspConnection(input, output);
        using var server = new OfframpLanguageServer(connection, options);
        return await server.LoopAsync(cancellationToken);
    }

    public void Dispose()
    {
        _engine?.Dispose();
        _engineLock.Dispose();
    }

    private IdeEnablement Enablement => Ide.Enablement.Resolve(_root ?? ".", _settings.Enabled);

    private async Task<int> LoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            JsonObject? message;
            try
            {
                message = await _connection.ReadAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }

            if (message is null)
            {
                return _shutdown ? 0 : 1;
            }

            var method = (string?)message["method"];
            var id = message["id"];
            if (method is null)
            {
                Respond(id, message);
                continue;
            }

            if (method == "exit")
            {
                return _shutdown ? 0 : 1;
            }

            if (id is null)
            {
                try
                {
                    await NotificationAsync(method, message["params"] as JsonObject, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await LogAsync($"{method} failed: {ex}");
                }

                continue;
            }

            if (method == "initialize")
            {
                await ReplyAsync(id, () => InitializeAsync(message["params"] as JsonObject, cancellationToken), cancellationToken);
                continue;
            }

            if (method == "shutdown")
            {
                await ReplyAsync(id, () => Task.FromResult(Shutdown()), cancellationToken);
                continue;
            }

            var key = Key(id);
            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _requests[key] = source;
            _ = Task.Run(async () =>
            {
                try
                {
                    await ReplyAsync(id, () => RequestAsync(method, message["params"] as JsonObject, source.Token), source.Token);
                }
                finally
                {
                    _requests.TryRemove(key, out _);
                    source.Dispose();
                }
            }, CancellationToken.None);
        }
    }

    // ----- requests -----

    private async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        if (_root is null)
        {
            throw new LspException(ServerNotInitialized, "The server has not been initialized.");
        }

        return method switch
        {
            "textDocument/codeLens" => await CodeLensAsync(parameters, cancellationToken),
            "textDocument/codeAction" => await CodeActionAsync(parameters, cancellationToken),
            "workspace/executeCommand" => await ExecuteCommandAsync(parameters, cancellationToken),
            "offramp/fileReport" => await FileReportAsync(parameters, cancellationToken),
            _ => throw new LspException(MethodNotFound, $"The server does not handle {method}."),
        };
    }

    private async Task<JsonNode?> InitializeAsync(JsonObject? parameters, CancellationToken cancellationToken)
    {
        var folder = (parameters?["workspaceFolders"] as JsonArray)?.OfType<JsonObject>().Select(f => (string?)f["uri"]).FirstOrDefault(u => u is not null)
            ?? (string?)parameters?["rootUri"];
        var directory = folder is not null ? PathOf(folder) : (string?)parameters?["rootPath"] ?? System.Environment.CurrentDirectory;
        _root = await _options.Git.FindRepositoryRootAsync(directory, cancellationToken) ?? Path.GetFullPath(directory);
        _canonicalRoot = RepoPaths.Canonical(_root);
        _settings = Settings(parameters?["initializationOptions"]);
        var capabilities = parameters?["capabilities"];
        _workDoneProgress = (bool?)capabilities?["window"]?["workDoneProgress"] ?? false;
        _showDocument = (bool?)capabilities?["window"]?["showDocument"]?["support"] ?? false;
        _codeLensRefresh = (bool?)capabilities?["workspace"]?["codeLens"]?["refreshSupport"] ?? false;

        var server = new JsonObject
        {
            ["textDocumentSync"] = new JsonObject { ["openClose"] = true, ["change"] = 1, ["save"] = new JsonObject { ["includeText"] = false } },
            ["codeLensProvider"] = new JsonObject { ["resolveProvider"] = false },
            ["codeActionProvider"] = new JsonObject { ["codeActionKinds"] = new JsonArray("quickfix", "refactor.move") },
        };
        if (!_settings.ClientCommands)
        {
            server["executeCommandProvider"] = new JsonObject { ["commands"] = new JsonArray([.. LspRender.Commands.Select(c => (JsonNode?)c)]) };
        }

        return new JsonObject
        {
            ["capabilities"] = server,
            ["serverInfo"] = new JsonObject { ["name"] = "offramp", ["version"] = _options.ServerVersion },
        };
    }

    private JsonNode? Shutdown()
    {
        _shutdown = true;
        return null;
    }

    private async Task<JsonNode?> CodeLensAsync(JsonObject? parameters, CancellationToken cancellationToken)
    {
        if (!_settings.CodeLens || await ReportAsync(parameters, cancellationToken) is not { } report)
        {
            return new JsonArray();
        }

        return LspRender.CodeLenses(report);
    }

    private async Task<JsonNode?> CodeActionAsync(JsonObject? parameters, CancellationToken cancellationToken)
    {
        if (await ReportAsync(parameters, cancellationToken) is not { } report)
        {
            return new JsonArray();
        }

        var range = parameters?["range"];
        var start = (int?)range?["start"]?["line"] ?? 0;
        var end = (int?)range?["end"]?["line"] ?? start;
        var context = parameters?["context"];
        var only = (context?["only"] as JsonArray)?.Select(o => (string?)o).OfType<string>().ToList();
        return LspRender.CodeActions(report, start, end, context?["diagnostics"] as JsonArray, only);
    }

    private async Task<JsonNode?> FileReportAsync(JsonObject? parameters, CancellationToken cancellationToken)
    {
        var report = await ReportAsync(parameters, cancellationToken);
        return report is null ? null : JsonSerializer.SerializeToNode(report, IdeJsonContext.Default.IdeFileReport);
    }

    /// <summary>The report for the document a request names (<c>textDocument.uri</c> or <c>uri</c>), or null.</summary>
    private async Task<IdeFileReport?> ReportAsync(JsonObject? parameters, CancellationToken cancellationToken)
    {
        var uri = (string?)parameters?["textDocument"]?["uri"] ?? (string?)parameters?["uri"];
        if (uri is null || File(uri) is not { } file || !Enablement.Enabled)
        {
            return null;
        }

        await _engineLock.WaitAsync(cancellationToken);
        try
        {
            return _engine is null ? null : await _engine.ReportAsync(file, cancellationToken);
        }
        finally
        {
            _engineLock.Release();
        }
    }

    private async Task<JsonNode?> ExecuteCommandAsync(JsonObject? parameters, CancellationToken cancellationToken)
    {
        var command = (string?)parameters?["command"];
        var argument = (parameters?["arguments"] as JsonArray)?.FirstOrDefault() as JsonObject;
        switch (command)
        {
            case LspRender.MoveCommand:
                if ((string?)argument?["file"] is not { } file || (string?)argument?["to"] is not { } to)
                {
                    throw new LspException(InvalidParams, "offramp.move takes { file, to }.");
                }

                var result = await MoveAsync(file, to, cancellationToken);
                return JsonSerializer.SerializeToNode(result, IdeJsonContext.Default.IdeMoveResult);
            case LspRender.ScanCommand:
                return await ScanAsync(cancellationToken);
            case LspRender.RefreshCommand:
                await LoadEngineAsync(askToScan: false, cancellationToken);
                return null;
            default:
                throw new LspException(InvalidParams, $"Unknown command {command}.");
        }
    }

    // ----- the move -----

    private async Task<IdeMoveResult> MoveAsync(string file, string to, CancellationToken cancellationToken)
    {
        var diagnostics = new DiagnosticBag();
        MovePlanDocument? plan;
        await _engineLock.WaitAsync(cancellationToken);
        try
        {
            if (_engine is null)
            {
                diagnostics.Report(DiagnosticCatalog.OFR0001, "There is no workspace model yet. Run `offramp scan` (Offramp: Scan), then move the file.");
                plan = null;
            }
            else
            {
                plan = _engine.PlanMove(file, to, diagnostics);
            }
        }
        finally
        {
            _engineLock.Release();
        }

        if (plan is null)
        {
            var reason = Reason(diagnostics) ?? $"{file} cannot move to {to}.";
            await ShowAsync(1, $"Offramp did not move {Path.GetFileName(file)}: {reason}", cancellationToken);
            return new IdeMoveResult { File = file, To = to, Refused = reason, Diagnostics = diagnostics.ToSortedList() };
        }

        var answer = await RequestClientAsync("window/showMessageRequest", new JsonObject
        {
            ["type"] = 3,
            ["message"] = Confirmation(plan),
            ["actions"] = new JsonArray(new JsonObject { ["title"] = "Move" }, new JsonObject { ["title"] = "Cancel" }),
        }, cancellationToken);
        if ((string?)answer?["title"] != "Move")
        {
            return new IdeMoveResult { File = file, To = to, Plan = plan, Refused = "Cancelled.", Diagnostics = diagnostics.ToSortedList() };
        }

        MoveApplyOutcome outcome;
        await using (var progress = await ProgressAsync($"Moving {Path.GetFileName(file)} to {IdeCheck.ProjectName(to)}", cancellationToken))
        {
            await _engineLock.WaitAsync(cancellationToken);
            try
            {
                outcome = await _engine!.ApplyMoveAsync(plan, diagnostics, progress.Sink, cancellationToken);
                _model = _engine.Staleness() is null ? "fresh" : "stale";
            }
            finally
            {
                _engineLock.Release();
            }
        }

        var applied = outcome.Result;
        if (applied.Applied && !applied.RolledBack && applied.Moved.Count > 0)
        {
            var moved = plan.Moves[0].To;
            await ShowAsync(3, $"Moved {Path.GetFileName(file)} to {IdeCheck.ProjectName(to)} ({moved}), staged with git mv. "
                + $"Undo with `offramp move rollback --journal {applied.Journal}`.", cancellationToken);
            if (_showDocument)
            {
                await RequestClientAsync("window/showDocument", new JsonObject { ["uri"] = UriOf(moved), ["takeFocus"] = true }, cancellationToken);
            }
        }
        else
        {
            await ShowAsync(1, $"Offramp did not move {Path.GetFileName(file)}: {Reason(diagnostics) ?? "the move was rolled back."}", cancellationToken);
        }

        await SendStatusAsync(cancellationToken);
        await RefreshAllAsync(cancellationToken);
        return new IdeMoveResult { File = file, To = to, Plan = plan, Apply = applied, Diagnostics = diagnostics.ToSortedList() };
    }

    /// <summary>What the user confirms: the rename, and every project-file edit.</summary>
    public static string Confirmation(MovePlanDocument plan)
    {
        var move = plan.Moves[0];
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Move {Path.GetFileName(move.File)} to {IdeCheck.ProjectName(plan.To)}? ");
        text.Append(CultureInfo.InvariantCulture, $"It is renamed with git mv to {move.To}; its contents, namespace included, do not change.");
        foreach (var edit in plan.ProjectEdits)
        {
            var project = IdeCheck.ProjectName(edit.Project);
            text.Append(' ').Append(edit.Kind switch
            {
                ProjectEditKind.AddProjectReference => $"{project} gets a project reference to {IdeCheck.ProjectName(edit.Value ?? "")}.",
                ProjectEditKind.AddInternalsVisibleTo => $"{project} lets {edit.Value} see its internals.",
                ProjectEditKind.AddPackageReference => $"{project} gets package {edit.Value} {edit.Version}.",
                _ => $"{project}'s project file changes ({edit.Kind}).",
            });
        }

        if (!string.Equals(plan.Verify, "none", StringComparison.OrdinalIgnoreCase))
        {
            text.Append(" Then the affected projects are built, and the move is undone if they fail.");
        }

        return text.ToString();
    }

    private static string? Reason(DiagnosticBag diagnostics) =>
        diagnostics.ToSortedList().Where(d => d.Severity >= Severity.Warning).Select(d => $"{d.Message} ({d.Code})").FirstOrDefault()
        ?? diagnostics.ToSortedList().Select(d => $"{d.Message} ({d.Code})").FirstOrDefault();

    // ----- scan and engine -----

    private async Task<JsonNode?> ScanAsync(CancellationToken cancellationToken)
    {
        int exit;
        await using (var progress = await ProgressAsync("Offramp: scanning the solution", cancellationToken))
        {
            exit = await _options.ScanAsync(_root!, progress.Sink, cancellationToken);
        }

        if (exit is not (0 or 1))
        {
            await ShowAsync(1, "offramp scan failed; run it in a terminal to see why.", cancellationToken);
        }

        await LoadEngineAsync(askToScan: false, cancellationToken);
        return exit;
    }

    private async Task LoadEngineAsync(bool askToScan, CancellationToken cancellationToken)
    {
        await LoadEngineLockedAsync(cancellationToken);
        await SendStatusAsync(cancellationToken);
        await RefreshAllAsync(cancellationToken);
        if (_model == "missing" && askToScan && !_scanAsked)
        {
            _scanAsked = true;
            _ = Task.Run(() => AskToScanAsync(cancellationToken), CancellationToken.None);
        }
    }

    private async Task LoadEngineLockedAsync(CancellationToken cancellationToken)
    {
        await _engineLock.WaitAsync(cancellationToken);
        try
        {
            _engine?.Dispose();
            _engine = null;
            _message = null;
            if (!Enablement.Enabled)
            {
                _model = "off";
                return;
            }

            var config = ConfigLoader.Load(new ConfigSources { RepositoryRoot = _root!, Environment = _options.Environment });
            if (!config.IsValid)
            {
                _model = "invalid-config";
                _message = "offramp.yml has problems (run `offramp doctor`): " + string.Join(" ", config.Diagnostics.Select(d => d.Message).Take(1));
                return;
            }

            var workspacePath = Path.Combine(_root!, config.Config.Paths.State, WorkspaceStore.FileName);
            if (!System.IO.File.Exists(workspacePath))
            {
                _model = "missing";
                _message = "No workspace model yet: `offramp scan` builds the solution once to create it.";
                return;
            }

            var model = WorkspaceStore.Read(workspacePath);
            var cache = new FileCache(Path.Combine(_root!, config.Config.Paths.State, "cache"));
            _engine = await IdeEngine.CreateAsync(new IdeEngineOptions
            {
                RepositoryRoot = _root!,
                Model = model,
                WorkspacePath = workspacePath,
                Config = config.Config,
                Settings = _settings,
                Git = _options.Git,
                Processes = _options.Processes,
                References = new TargetReferenceResolver(_root!, _options.Processes, cache),
                Time = _options.Time,
            }, cancellationToken);
            foreach (var (file, document) in _documents)
            {
                _engine.Workspace.SetOpenDocument(file, document.Text);
            }

            _baseCheckedAt = _options.Time.GetUtcNow();
            var stale = _engine.Staleness();
            _model = stale is null ? "fresh" : "stale";
            _message = stale is null ? null : $"The workspace model is stale ({stale}); moves need `offramp scan`.";
        }
        finally
        {
            _engineLock.Release();
        }
    }

    private async Task AskToScanAsync(CancellationToken cancellationToken)
    {
        try
        {
            var answer = await RequestClientAsync("window/showMessageRequest", new JsonObject
            {
                ["type"] = 3,
                ["message"] = "Offramp suggests where new code can live once it has a workspace model. `offramp scan` builds the solution once to create it. Scan now?",
                ["actions"] = new JsonArray(new JsonObject { ["title"] = "Scan now" }, new JsonObject { ["title"] = "Not now" }),
            }, cancellationToken);
            if ((string?)answer?["title"] == "Scan now")
            {
                await ScanAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await LogAsync($"scan prompt failed: {ex.Message}");
        }
    }

    private async Task SendStatusAsync(CancellationToken cancellationToken)
    {
        var enablement = Enablement;
        var counterparts = _engine is null
            ? new JsonArray()
            : JsonSerializer.SerializeToNode(_engine.Counterparts.ToList(), IdeJsonContext.Default.ListProjectCounterparts)!;
        await NotifyAsync("offramp/status", new JsonObject
        {
            ["enabled"] = enablement.Enabled,
            ["reason"] = enablement.Reason,
            ["model"] = _model,
            ["counterparts"] = counterparts,
            ["message"] = _message,
        }, cancellationToken);
    }

    // ----- notifications -----

    private async Task NotificationAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "initialized":
                _ = Task.Run(() => LoadEngineAsync(askToScan: true, cancellationToken), CancellationToken.None);
                break;
            case "$/cancelRequest":
                if (parameters?["id"] is { } id && _requests.TryGetValue(Key(id), out var source))
                {
                    await source.CancelAsync();
                }

                break;
            case "textDocument/didOpen":
                await OpenAsync((string?)parameters?["textDocument"]?["uri"], (string?)parameters?["textDocument"]?["text"], cancellationToken);
                break;
            case "textDocument/didChange":
                var text = (parameters?["contentChanges"] as JsonArray)?.OfType<JsonObject>().Select(c => (string?)c["text"]).LastOrDefault(t => t is not null);
                await OpenAsync((string?)parameters?["textDocument"]?["uri"], text, cancellationToken);
                break;
            case "textDocument/didSave":
                if (File((string?)parameters?["textDocument"]?["uri"]) is { } saved)
                {
                    await RefreshBaseAsync(force: true, cancellationToken);
                    Schedule(saved, TimeSpan.Zero);
                }

                break;
            case "textDocument/didClose":
                await CloseAsync((string?)parameters?["textDocument"]?["uri"], cancellationToken);
                break;
            case "workspace/didChangeWatchedFiles":
                await WatchedFilesAsync(parameters?["changes"] as JsonArray, cancellationToken);
                break;
            case "workspace/didChangeConfiguration":
                if (parameters?["settings"]?["offramp"] is { } settings)
                {
                    _settings = Settings(settings) with { ClientCommands = _settings.ClientCommands };
                    await LoadEngineAsync(askToScan: true, cancellationToken);
                }

                break;
        }
    }

    private async Task OpenAsync(string? uri, string? text, CancellationToken cancellationToken)
    {
        if (uri is null || text is null || File(uri) is not { } file || !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _documents[file] = (uri, text);
        await _engineLock.WaitAsync(cancellationToken);
        try
        {
            _engine?.Workspace.SetOpenDocument(file, text);
        }
        finally
        {
            _engineLock.Release();
        }

        Schedule(file, _options.Debounce);
    }

    private async Task CloseAsync(string? uri, CancellationToken cancellationToken)
    {
        if (uri is null || File(uri) is not { } file || !_documents.TryRemove(file, out _))
        {
            return;
        }

        if (_debounce.TryRemove(file, out var pending))
        {
            await pending.CancelAsync();
        }

        await _engineLock.WaitAsync(cancellationToken);
        try
        {
            _engine?.Workspace.CloseDocument(file);
        }
        finally
        {
            _engineLock.Release();
        }

        await NotifyAsync("textDocument/publishDiagnostics", new JsonObject { ["uri"] = uri, ["diagnostics"] = new JsonArray() }, cancellationToken);
    }

    private async Task WatchedFilesAsync(JsonArray? changes, CancellationToken cancellationToken)
    {
        var files = changes?.OfType<JsonObject>().Select(c => File((string?)c["uri"])).OfType<string>().ToList() ?? [];
        if (files.Count == 0)
        {
            return;
        }

        if (files.Any(f => f.EndsWith("/workspace.json", StringComparison.Ordinal) || f == "workspace.json" || Path.GetFileName(f) == ConfigLoader.DefaultFileName))
        {
            await LoadEngineAsync(askToScan: false, cancellationToken);
            return;
        }

        await _engineLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var file in files)
            {
                _engine?.Workspace.FileChanged(file);
            }

            if (_engine is not null && files.Any(f => WorkspaceInputs.IsInput(Path.GetFileName(f))))
            {
                var stale = _engine.Staleness();
                _model = stale is null ? "fresh" : "stale";
                _message = stale is null ? null : $"The workspace model is stale ({stale}); moves need `offramp scan`.";
            }
        }
        finally
        {
            _engineLock.Release();
        }

        await SendStatusAsync(cancellationToken);
        await RefreshAllAsync(cancellationToken);
    }

    // ----- diagnostics -----

    private void Schedule(string file, TimeSpan delay)
    {
        var source = new CancellationTokenSource();
        _debounce.AddOrUpdate(file, source, (_, previous) =>
        {
            previous.Cancel();
            return source;
        });
        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, source.Token);
                }

                await PublishAsync(file, source.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await LogAsync($"analysis of {file} failed: {ex}");
            }
        }, CancellationToken.None);
    }

    private async Task PublishAsync(string file, CancellationToken cancellationToken)
    {
        if (!_documents.TryGetValue(file, out var document))
        {
            return;
        }

        await RefreshBaseAsync(force: false, cancellationToken);
        JsonArray diagnostics;
        await _engineLock.WaitAsync(cancellationToken);
        try
        {
            diagnostics = _engine is null || !Enablement.Enabled
                ? []
                : LspRender.Diagnostics(await _engine.ReportAsync(file, cancellationToken));
        }
        finally
        {
            _engineLock.Release();
        }

        await NotifyAsync("textDocument/publishDiagnostics", new JsonObject { ["uri"] = document.Uri, ["diagnostics"] = diagnostics }, cancellationToken);
    }

    /// <summary>Re-resolves the new-code base when asked, or when it was last resolved more than half a minute ago (a commit, a checkout).</summary>
    private async Task RefreshBaseAsync(bool force, CancellationToken cancellationToken)
    {
        var now = _options.Time.GetUtcNow();
        if (!force && now - _baseCheckedAt < TimeSpan.FromSeconds(30))
        {
            return;
        }

        await _engineLock.WaitAsync(cancellationToken);
        try
        {
            if (_engine is not null)
            {
                await _engine.RefreshNewCodeAsync(cancellationToken);
                _baseCheckedAt = now;
            }
        }
        finally
        {
            _engineLock.Release();
        }
    }

    private async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        foreach (var file in _documents.Keys)
        {
            Schedule(file, TimeSpan.Zero);
        }

        if (_codeLensRefresh)
        {
            try
            {
                await RequestClientAsync("workspace/codeLens/refresh", null, cancellationToken);
            }
            catch (LspException)
            {
            }
        }
    }

    // ----- protocol plumbing -----

    private async Task ReplyAsync(JsonNode id, Func<Task<JsonNode?>> handler, CancellationToken cancellationToken)
    {
        JsonObject response;
        try
        {
            var result = await handler();
            response = new JsonObject { ["id"] = id.DeepClone(), ["result"] = result };
        }
        catch (OperationCanceledException)
        {
            response = Error(id, RequestCancelled, "The request was cancelled.");
        }
        catch (LspException ex)
        {
            response = Error(id, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            await LogAsync(ex.ToString());
            response = Error(id, -32603, $"{ex.GetType().Name}: {ex.Message}");
        }

        await _connection.WriteAsync(response, CancellationToken.None);
    }

    private static JsonObject Error(JsonNode id, int code, string message) =>
        new() { ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private void Respond(JsonNode? id, JsonObject message)
    {
        if (id is null || !_pending.TryRemove(Key(id), out var completion))
        {
            return;
        }

        if (message["error"] is JsonObject error)
        {
            completion.TrySetException(new LspException((int?)error["code"] ?? -32603, (string?)error["message"] ?? "error"));
        }
        else
        {
            completion.TrySetResult(message["result"]);
        }
    }

    private async Task<JsonNode?> RequestClientAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        var id = "offramp-" + Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[Key(JsonValue.Create(id))] = completion;
        var message = new JsonObject { ["id"] = id, ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        await _connection.WriteAsync(message, cancellationToken);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return await completion.Task;
    }

    private Task NotifyAsync(string method, JsonObject parameters, CancellationToken cancellationToken) =>
        _connection.WriteAsync(new JsonObject { ["method"] = method, ["params"] = parameters }, cancellationToken);

    private Task ShowAsync(int type, string message, CancellationToken cancellationToken) =>
        NotifyAsync("window/showMessage", new JsonObject { ["type"] = type, ["message"] = message }, cancellationToken);

    private async Task LogAsync(string message)
    {
        await _options.Log.WriteLineAsync("offramp ide serve: " + message);
        await _options.Log.FlushAsync();
    }

    private async Task<LspProgress> ProgressAsync(string title, CancellationToken cancellationToken)
    {
        if (!_workDoneProgress)
        {
            return new LspProgress(null, null);
        }

        var token = "offramp-progress-" + Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);
        try
        {
            await RequestClientAsync("window/workDoneProgress/create", new JsonObject { ["token"] = token }, cancellationToken);
        }
        catch (LspException)
        {
            return new LspProgress(null, null);
        }

        var progress = new LspProgress(token, this);
        progress.Send(new JsonObject { ["kind"] = "begin", ["title"] = title, ["cancellable"] = false });
        return progress;
    }

    private static IdeSettings Settings(JsonNode? node)
    {
        IdeSettings? settings = null;
        try
        {
            settings = node is JsonObject ? node.Deserialize(IdeJsonContext.Default.IdeSettings) : null;
        }
        catch (JsonException)
        {
        }

        settings ??= new IdeSettings();
        return settings with
        {
            Enabled = settings.Enabled ?? "auto",
            ProjectMap = settings.ProjectMap?.Where(e => e is not null && !string.IsNullOrWhiteSpace(e.From) && !string.IsNullOrWhiteSpace(e.To)).ToList() ?? [],
            NewCode = settings.NewCode ?? new IdeNewCodeSettings(),
        };
    }

    /// <summary>The repository-relative path of a <c>file:</c> URI inside the repository, or null.</summary>
    private string? File(string? uri)
    {
        if (uri is null || _root is null || !uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = PathOf(uri);
        var relative = RepoPaths.ToRepositoryRelative(_root, path);
        if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            relative = RepoPaths.ToRepositoryRelative(_canonicalRoot!, RepoPaths.Canonical(path));
        }

        return relative.StartsWith("../", StringComparison.Ordinal) || relative == ".." || Path.IsPathRooted(relative) || relative.Length == 0 ? null : relative;
    }

    private static string PathOf(string uri) => Path.GetFullPath(new Uri(uri).LocalPath);

    private string UriOf(string file) => new Uri(RepoPaths.ToAbsolute(_root!, file)).AbsoluteUri;

    private static string Key(JsonNode id) => id.ToJsonString();

    /// <summary>Work-done progress for one operation; a sink for the engine, ended when disposed.</summary>
    private sealed class LspProgress(string? token, OfframpLanguageServer? server) : IAsyncDisposable
    {
        public IProgressSink Sink { get; } = token is null || server is null ? NullProgressSink.Instance : new ProgressSink(token, server);

        public void Send(JsonObject value)
        {
            if (token is not null && server is not null)
            {
                server._connection.WriteAsync(new JsonObject { ["method"] = "$/progress", ["params"] = new JsonObject { ["token"] = token, ["value"] = value } }, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
        }

        public ValueTask DisposeAsync()
        {
            Send(new JsonObject { ["kind"] = "end" });
            return ValueTask.CompletedTask;
        }

        private sealed class ProgressSink(string token, OfframpLanguageServer server) : IProgressSink
        {
            public IProgressPhase BeginPhase(string name, int index, int of)
            {
                Report(name, null);
                return new Phase(name, this);
            }

            public void Log(ProgressLevel level, string message)
            {
            }

            public void Report(string message, int? percentage)
            {
                var value = new JsonObject { ["kind"] = "report", ["message"] = message };
                if (percentage is { } p)
                {
                    value["percentage"] = p;
                }

                new LspProgress(token, server).Send(value);
            }

            private sealed class Phase(string name, ProgressSink sink) : IProgressPhase
            {
                public string Name { get; } = name;

                public void Report(int current, int total, string? item = null) =>
                    sink.Report(item is null ? Name : $"{Name}: {item}", total > 0 ? Math.Clamp(current * 100 / total, 0, 100) : null);

                public void Dispose()
                {
                }
            }
        }
    }
}

/// <summary>A JSON-RPC error to send back, or one the client sent.</summary>
public sealed class LspException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

namespace Offramp.Scaffolding.Web;

/// <summary>A controller action.</summary>
public sealed record WebAction
{
    public required string Name { get; init; }

    /// <summary>HTTP methods from attributes or the Web API naming convention; empty means any.</summary>
    public IReadOnlyList<string> HttpMethods { get; init; } = [];

    /// <summary>Attribute route templates, combined with the controller's prefix.</summary>
    public IReadOnlyList<string> Routes { get; init; } = [];

    public IReadOnlyList<string> Filters { get; init; } = [];

    public required string File { get; init; }

    public required int Line { get; init; }
}

/// <summary>An MVC or Web API controller.</summary>
public sealed record WebController
{
    public required string Name { get; init; }

    /// <summary>The class, fully qualified.</summary>
    public required string Type { get; init; }

    /// <summary><c>mvc</c> or <c>webapi</c>.</summary>
    public required string Kind { get; init; }

    public string? Area { get; init; }

    public string? RoutePrefix { get; init; }

    public IReadOnlyList<string> Filters { get; init; } = [];

    public required string File { get; init; }

    public IReadOnlyList<WebAction> Actions { get; init; } = [];
}

/// <summary>A convention route (MapRoute, MapHttpRoute, an area's MapRoute, IgnoreRoute).</summary>
public sealed record WebRoute
{
    public required string Name { get; init; }

    public required string Template { get; init; }

    /// <summary><c>mvc</c>, <c>webapi</c>, or <c>ignore</c>.</summary>
    public required string Kind { get; init; }

    public string? Area { get; init; }

    /// <summary>Defaults as <c>name = value</c>, in source order; <c>?</c> marks an optional parameter.</summary>
    public IReadOnlyList<string> Defaults { get; init; } = [];

    public required string File { get; init; }

    public required int Line { get; init; }
}

/// <summary>An HttpModule or HttpHandler: its class, and where web.config registers it.</summary>
public sealed record WebComponent
{
    public required string Name { get; init; }

    /// <summary>The class, fully qualified (as web.config names it when the class is not in the project).</summary>
    public required string Type { get; init; }

    /// <summary>The class's file, or null when it is not in the project.</summary>
    public string? File { get; init; }

    /// <summary>Handlers: the path and verbs web.config maps to it.</summary>
    public string? Path { get; init; }

    public string? Verb { get; init; }

    /// <summary>Where web.config registers it: <c>system.web</c>, <c>system.webServer</c>; empty when only the code declares it.</summary>
    public IReadOnlyList<string> RegisteredIn { get; init; } = [];
}

/// <summary>A source file's use of System.Web.* APIs.</summary>
public sealed record WebApiSurface
{
    public required string File { get; init; }

    /// <summary>The System.Web.* types it uses, fully qualified, sorted.</summary>
    public IReadOnlyList<string> Types { get; init; } = [];
}

/// <summary>A place that uses session state.</summary>
public sealed record WebSite(string File, int Line, string What);

/// <summary>The web.config settings that shape the migration.</summary>
public sealed record WebSettings
{
    /// <summary>authentication mode (Forms, Windows, None), else null.</summary>
    public string? Authentication { get; init; }

    public string? LoginUrl { get; init; }

    /// <summary>sessionState mode (InProc, StateServer, SQLServer, Off), else null.</summary>
    public string? SessionState { get; init; }

    public bool MachineKey { get; init; }

    public string? CustomErrors { get; init; }

    /// <summary>The web.config sections present, in document order.</summary>
    public IReadOnlyList<string> Sections { get; init; } = [];
}

/// <summary>The result of <c>offramp web inventory</c>.</summary>
public sealed record WebInventoryResult
{
    public required string Project { get; init; }

    /// <summary><c>mvc</c>, <c>webapi</c>, <c>webforms</c>, a combination joined with <c>+</c>, or <c>none</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The application's URL from the project's IIS settings, else null.</summary>
    public string? Url { get; init; }

    public IReadOnlyList<WebController> Controllers { get; init; } = [];

    public IReadOnlyList<WebRoute> Routes { get; init; } = [];

    /// <summary>Whether attribute routes are enabled (MapMvcAttributeRoutes, MapHttpAttributeRoutes).</summary>
    public IReadOnlyList<string> AttributeRouting { get; init; } = [];

    public IReadOnlyList<string> GlobalFilters { get; init; } = [];

    public IReadOnlyList<string> Areas { get; init; } = [];

    public IReadOnlyList<WebComponent> Modules { get; init; } = [];

    public IReadOnlyList<WebComponent> Handlers { get; init; } = [];

    /// <summary>The Global.asax class's Application_* and Session_* handlers.</summary>
    public IReadOnlyList<string> GlobalAsax { get; init; } = [];

    /// <summary>Bundles registered with System.Web.Optimization, else empty.</summary>
    public IReadOnlyList<string> Bundles { get; init; } = [];

    /// <summary>Web Forms pages, user controls, and master pages, repository-relative.</summary>
    public IReadOnlyList<string> WebForms { get; init; } = [];

    public IReadOnlyList<WebSite> Session { get; init; } = [];

    /// <summary>Output caching: [OutputCache] actions and controllers.</summary>
    public IReadOnlyList<WebSite> OutputCache { get; init; } = [];

    public required WebSettings Settings { get; init; }

    public IReadOnlyList<WebApiSurface> SystemWeb { get; init; } = [];
}

/// <summary>An action <c>web scaffold</c> left to the legacy application, and why.</summary>
public sealed record WebUnported(string Action, string Reason);

/// <summary>A controller and what <c>web scaffold</c> did with its actions.</summary>
public sealed record WebPortedController
{
    public required string Type { get; init; }

    /// <summary>The new file, or null when no action was ported.</summary>
    public string? File { get; init; }

    public IReadOnlyList<string> Ported { get; init; } = [];

    public IReadOnlyList<WebUnported> Unported { get; init; } = [];

    /// <summary>Attributes left out (filters with no ASP.NET Core counterpart).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>The result of <c>offramp web scaffold</c>.</summary>
public sealed record WebScaffoldResult
{
    public required string Project { get; init; }

    /// <summary>The new project file.</summary>
    public required string NewProject { get; init; }

    public required string TargetFramework { get; init; }

    /// <summary><c>yarp</c> or <c>none</c>.</summary>
    public required string Proxy { get; init; }

    public bool Adapters { get; init; }

    /// <summary>The legacy application's URL the proxy forwards to.</summary>
    public string? LegacyUrl { get; init; }

    public IReadOnlyList<WebPortedController> Controllers { get; init; } = [];

    /// <summary>Convention routes as ASP.NET Core maps them.</summary>
    public IReadOnlyList<string> Routes { get; init; } = [];

    /// <summary>Middleware stubs for HttpModules.</summary>
    public IReadOnlyList<string> Middleware { get; init; } = [];

    /// <summary>Endpoint stubs for HttpHandlers (OFR4202).</summary>
    public IReadOnlyList<string> Endpoints { get; init; } = [];

    /// <summary>Web Forms files left to the legacy application (OFR4201).</summary>
    public IReadOnlyList<string> WebForms { get; init; } = [];

    /// <summary>With <c>--proxy none</c>: the paths the new application serves, for the ingress.</summary>
    public IReadOnlyList<string> IngressPaths { get; init; } = [];

    /// <summary>Files of the legacy project the new one compiles as links.</summary>
    public IReadOnlyList<string> LinkedSources { get; init; } = [];

    public IReadOnlyList<string> Files { get; init; } = [];

    public IReadOnlyList<string> NextSteps { get; init; } = [];

    public bool Applied { get; init; }

    public string? Journal { get; init; }

    public string? Preview { get; init; }
}

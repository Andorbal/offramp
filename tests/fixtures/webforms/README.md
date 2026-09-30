# webforms

A Web Forms site in the shape DotNetNuke modules have, in three plain `net48` SDK-style
projects so it builds on every OS:

- `Portal.Controls`: `ModuleBase`, a `System.Web.UI.UserControl` that module controls
  derive from (DotNetNuke's `PortalModuleBase`), with a helper that takes a `Control`, and
  `RequestExtensions.IsSecure`, an extension method over `HttpRequestBase`.
- `Portal.Utilities`: a legacy (non-SDK) Visual Basic library the modules call, as DotNetNuke's
  WebUtility is. Legacy `vbc` takes `mscorlib` from `/sdkpath` without naming it, so the compiler
  log's reconstruction of it has no core library until Offramp adds one. `Directory.Build.props`
  gives legacy projects the reference assemblies as a package, as in `legacy-csproj`.
- `Portal.Modules`: a web project (it has a `web.config`) with markup and code-behind:
  - `EditSettings.ascx` / `EditSettings.ascx.cs`: derives from `ModuleBase` in the other
    project. Compiled against the target, whose web projects also reference ASP.NET Core,
    Roslyn reports the missing `UserControl` at every name looked up inside the class
    (`Convert`, `EventArgs`, `ModuleBase`), and at the call to the helper whose signature
    has `Control`. `audit api` must report only what the class really uses from `System.Web`
    (`ViewState`, `ClientID`), and nothing from the Visual Basic library, which it cannot
    compile against the target.
  - `Default.aspx` / `Default.aspx.cs`: `DefaultPage` is named only by the page's
    `Inherits` attribute, so `audit dead-code` must not report it.
  - `ModuleRoutes.cs`: named by nothing, but it implements `IModuleRoutes`, which
    `RouteRegistry` finds types by with `typeof(IModuleRoutes).IsAssignableFrom(t)`, as
    DotNetNuke finds its `IServiceRouteMapper`s: `audit dead-code` rates it low.
  - `UpgradeController.cs`: named only by `Portal.Modules.dnn`, a DotNetNuke manifest (XML
    under its own extension, without an XML declaration): `audit dead-code` rates it low.
  - `Leftover.cs`: named by nothing, in code or markup, so it is dead code.
  - `LinkBuilder.cs`: calls `IsSecure()` on an `HttpRequestBase`. The extension is missing on
    the target because `HttpRequestBase` is, so `audit api` attributes it to System.Web, not
    to Portal.Controls (SmartStoreNET had 133 such findings "in" its own assemblies).
  - `PortalException.cs`: derives from `HttpException`, missing on the target, and reads
    `ErrorCode`, which it inherits from `ExternalException`. The target has `ErrorCode`, so it is
    not a finding, although Roslyn cannot look it up through the missing base type.

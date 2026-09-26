# mvc5 fixture

An ASP.NET MVC 5 + Web API 2 application with a little of everything System.Web, for
`web inventory` and `web scaffold`. It builds on any OS like `legacy-csproj`: the .NET
Framework reference assemblies come as a package, the tests fill `packages/` from
packages.config (`PackagesConfigRestore`), and `WebApplication.targets` is imported only
when Visual Studio's web tooling is installed.

| File | What it exercises |
|---|---|
| `App_Start/RouteConfig.cs` | `IgnoreRoute`, a named convention route with defaults (`catalog/{category}`), the default route, `MapMvcAttributeRoutes` |
| `App_Start/WebApiConfig.cs` | `MapHttpAttributeRoutes`, the `DefaultApi` convention route |
| `App_Start/FilterConfig.cs` | a global filter (`HandleErrorAttribute`) |
| `Areas/Admin` | an area registration and an `[Authorize(Roles)]` controller that renders a view |
| `Controllers/HomeController.cs` | actions that port (`Content`, `Json` with `JsonRequestBehavior`), one that renders a view, one using `ViewBag`, one using `Session`, one only the compiler rejects (`AppDomainSetup.ConfigurationFile`); `[OutputCache]`, `[ValidateAntiForgeryToken]` |
| `Controllers/OrdersController.cs` | `[RoutePrefix]`/`[Route]` attribute routes, `HttpNotFound`, `[Authorize]` |
| `Controllers/Api/ProductsController.cs` | a Web API controller with attribute routes, verbs by name and by attribute, `IHttpActionResult`, a model type from elsewhere in the project (linked into the new project) |
| `Controllers/Api/StockController.cs` | a conventionally routed Web API action using `HttpContext.Current` (not ported) |
| `Modules/RequestTimingModule.cs` | an HttpModule registered in both `system.web` and `system.webServer` (a middleware stub) |
| `Handlers/ThumbnailHandler.cs` | an HttpHandler mapped to `thumbnail.axd` (an endpoint stub, OFR4202) |
| `Legacy/Report.aspx`, `Controls/Header.ascx` | Web Forms (OFR4201) |
| `Global.asax.cs` | `Application_Start`, `Application_BeginRequest`, `Session_Start`, `Application_Error` |
| `Web.config` | Forms authentication, InProc session, a machine key, custom errors |

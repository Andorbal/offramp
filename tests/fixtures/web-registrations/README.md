# web-registrations fixture

An ASP.NET MVC 5 site whose routes, areas, filters, and bundles are registered the way
SmartStoreNET 4.2 and nopCommerce 3.90 register them (field test
`docs/field-tests/2026-09-smartstorenet-4.2.0.md`, P1 #11 and P2; ADR 0059): through the
codebase's own helpers, in a library the site references, and with Autofac. It builds on any
OS like `mvc5`: the reference assemblies come as a package and the tests fill `packages/`
from packages.config.

| File | What it exercises |
|---|---|
| `Store.Framework/Routing/LocalizedRouteExtensions.cs` | `MapLocalizedRoute` overloads that end, three and four calls down, in one method that constructs a `LocalizedRoute` and `routes.Add`s it; `CreateLocalizedRoute`, which constructs one without adding it |
| `Store.Framework/Routing/AreaRouteExtensions.cs` | a helper around `MapRoute` that sets `DataTokens["area"]` from its parameter |
| `Store.Framework/Routing/AreaRouteFilter.cs` | a copy of the route table the library builds and `Add`s routes to, which is not a registration |
| `Store.Framework/WebApi/WebApiStartup.cs` | `MapHttpRoute` and `MapODataServiceRoute` in the library, names from static properties |
| `Store.Framework/DependencyRegistrar.cs` | Autofac's `AsExceptionFilterFor`, `AsActionFilterFor`, `AsResultFilterFor` for controller base classes, chained |
| `Store.Web/Infrastructure/StoreRoutes.cs` | `IgnoreRoute`; a local function around `MapRoute` whose template starts with a path read from the settings; `MapLocalizedRoute` with three, four, and five arguments; a plain `MapRoute` |
| `Store.Web/Infrastructure/SeoRoutes.cs` | `CreateLocalizedRoute` with a template from a local function; `routes.Add` of routes from a registry (name and template computed) |
| `Store.Web/Infrastructure/AreaRoutes.cs` | areas declared by routes: an `area` default, a chained `.DataTokens["area"]`, the helper |
| `Store.Web/Infrastructure/BundleConfig.cs` | `bundles.Add(new ScriptBundle(...))` and `bundles.Add(scriptBundle)` with the bundle in a local |
| `Store.Web/Infrastructure/DependencyRegistrar.cs` | an Autofac result filter for one action (`x => x.Index()`) |
| `Store.Web/Controllers/HomeController.cs` | a controller on the library's base class, which renders a view |
| `Store.Web/Controllers/AccountController.cs` | actions that port with their System.Web APIs mapped: `EmptyResult`, `ModelState.AddModelError`/`IsValid`, `TempData`, `ViewData`, `ViewBag`, `Url.Action`, `RedirectResult`, `HttpUnauthorizedResult`, `FormCollection` |
| `Store.Web/Areas/Admin/Controllers/DashboardController.cs` | a controller of the area the admin route declares |

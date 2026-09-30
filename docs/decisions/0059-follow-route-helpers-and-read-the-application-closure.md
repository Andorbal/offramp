# 0059. Follow the codebase's route helpers, read the application's referenced projects, and list container filters

- Status: accepted
- Date: 2026-09-30
- Spec section: `docs/spec/commands/scaffold.md` (`web inventory`, `web scaffold`)

## Context

The spec reads convention routes "from `RouteConfig`/`WebApiConfig` parsing", filters
"global and per-controller", and areas from `AreaRegistration` classes. The SmartStoreNET
field test (`docs/field-tests/2026-09-smartstorenet-4.2.0.md`, P1 #11) found 8 of the 68
routes the site declares: 57 go through the codebase's own helpers (`MapLocalizedRoute`,
`CreateLocalizedRoute`, `MapGenericPathRoute`, a local function around `MapRoute`), the
Web API and OData routes and 15 filters are registered in a library the site references,
the filters through Autofac, and the areas are declared by their routes (`area = "Admin"`,
`.DataTokens["area"] = "SmartStore.Tax"`). nopCommerce has the same shape. The spec does not
say which projects' registrations belong to an application, how far a helper is followed,
what a value computed at run time looks like in the inventory, or where filters registered
with a container go.

## Decision

**Which projects.** An application's registrations are those in its own sources and in the
C# libraries it references, directly or through other libraries: they run in the
application's process (`AreaRegistration.RegisterAllAreas`, startup tasks, Autofac
modules). A referenced project that is itself a web application or a test project is not a
library: SmartStoreNET's WebApi plugin references the site, whose 71 routes are the site's,
not the plugin's. Projects that the application loads at run time without referencing them
(plugins) are not included here; which projects a web application hosts is a separate
question for the workspace model.

**Registrations.** A route registration is a call of `MapRoute` (also an
`AreaRegistrationContext`'s), `MapHttpRoute`, `IgnoreRoute`, `RouteCollection.Add` with a
`Route` object (constructed there or in a local), or `MapODataServiceRoute`/`MapODataRoute`
(kind `odata`). Its name, template, defaults, and area are read with the semantic model:
literals and constants, string concatenation and interpolation, locals assigned once,
static get-only properties and static read-only fields that return a literal. When a value
comes from a parameter of the method or local function the call is in, that method is a
helper: each of its calls in the application and its libraries is a route, with the call's
arguments in place of the parameters. Helpers are followed up to five calls away from the
registration: SmartStoreNET's `MapLocalizedRoute(name, url, defaults)` is four calls from
its `routes.Add`. The helper is found bottom-up, from the registration calls to the calls of
the methods they are in, so only calls named like a registration or a helper are bound.
Conditions are not evaluated: a registration under `if (add)` or
`if (!Routes.ContainsKey(...))` counts, which lists SmartStoreNET's `CreateLocalizedRoute`
routes (created there, added to the route table later).

**Computed values.** A template that is not a literal is `(computed)`, and `computed` holds
the C# that computes it, with a helper's parameters replaced by the call's arguments
(`mediaPublicPath + "uploaded/{*path}"`); a computed name is `(computed)`. `helper` names
the codebase's method the registration goes through. `web scaffold` cannot write a
`MapControllerRoute` for a computed template: it leaves the route to the proxy and says so
(`OFR4205`).

**Areas.** A route's area is its `AreaRegistration`'s `AreaName`, else a
`DataTokens["area"]` assignment (or `DataTokens.Add("area", ...)`) on the route the call
returns, chained or through the local it is stored in, or a `DataTokens` dictionary built
with an area, else its `area` default. `areas` lists the `AreaRegistration` classes and every
area a route names.

**Filters.** Autofac's `As{Action,Result,Exception,Authorization,Authentication}Filter[Override]For<TController>`
(MVC and Web API) are listed in a new `containerFilters` array: the filter class (the
registration builder's `TLimit`, down the fluent chain to `RegisterType<T>()`), its kind, the
controller type argument (often a base class), and the action the selector names.
`GlobalFilters.Filters.Add` stays in `globalFilters`, now also from the libraries.

## Alternatives considered

- **One level of helper** (the field notes' suggestion): misses SmartStoreNET's overload
  chains, which are three and four calls deep.
- **Top-down**: analyzing the body of every method a registration file calls. It binds
  most of the libraries' code; bottom-up binds the calls named like registrations (and the
  `Add` calls) and the helpers' own calls.
- **Folding computed templates into a pattern** (`{mediaPublicPath}uploaded/{*path}`): the
  placeholders read as route parameters. The C# expression cannot be mistaken for a template.
- **Container filters in `globalFilters`, or on the controllers they name**: they are neither
  global nor declared on the controller, and a registration for a base class applies to
  every controller deriving from it, in any project.
- **Evaluating conditions**: `if (add)` is a parameter, `ContainsKey` a run-time check;
  listing the registration is the conservative answer for a migration inventory.

## Consequences

The inventory loads the compilations of the referenced projects, so it takes longer on a
large solution, and it lists a library's registrations even when the application never
calls the code that makes them (a helper nobody calls is not listed). The JSON contract
gains `computed` and `helper` on routes, the `odata` kind, and `containerFilters`; routes
are ordered by file and line across projects. Helpers deeper than five calls, registrations
through delegates or reflection, and route objects built by factories other than a
`Route` constructor stay computed or unlisted.

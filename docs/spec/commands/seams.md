# Seams: `seams`, `extract interface`, `remote`

For code that will never port. Find the smallest boundary around it, put an
interface at that boundary, and optionally turn the boundary into a network
call to a Windows-hosted service.

## `seams`

```
offramp seams --project P [--unportable-from audit|list] [--symbols NS.Type,...] [--max-cut N] [--out seams.json] [--format json|table|dot|html]
```

Inputs: the set of **unportable symbols** in `P`, from `audit api`
(error-level `OFR3001`/`OFR3004`–`3009` findings, plus `OFR3002` when the
target is not `-windows`) or an explicit list.

Method:
1. Build the **type reference graph** of `P` from the semantic model: nodes are
   named types; an edge `A → B` exists when any member of `A` references any
   member of `B` (calls, field/property types, base types, generic arguments,
   attributes). Edge weights = number of distinct referencing members.
2. Mark **tainted** types: those that directly use an unportable symbol, then
   propagate: a type is tainted if it *inherits from* a tainted type, or holds
   a tainted type in a field/property/base/parameter of a public member
   (structural taint). Mere calls do not taint the caller; that is where a
   seam can go.
3. Compute **strongly connected components**; every type in an SCC with a
   tainted type is in the same partition (they move together). Contract SCCs.
4. On the contracted DAG, find the **minimum cut** between the clean side and
   the tainted side (max-flow with member counts as capacities, sources = clean
   entry points that reach taint, sink = tainted set). The cut edges are the
   seam: caller type → tainted type pairs. Group by tainted type: each is a
   candidate interface, its members = the members actually invoked across the
   cut.
5. Rank seams by (interface member count, number of callers, whether
   parameters are wire-friendly). Report **articulation points**: single types
   whose extraction disconnects the taint entirely.

Output:

```jsonc
{
  "project": "src/Foo/Foo.csproj",
  "tainted": [ { "type": "Foo.Directory.AdLookup", "reason": ["System.DirectoryServices.DirectorySearcher"], "loc": 412 } ],
  "partitions": [ { "id": 1, "types": ["Foo.Directory.AdLookup", "Foo.Directory.AdEntry"], "loc": 530 } ],
  "seams": [
    {
      "id": "seam-1",
      "boundaryType": "Foo.Directory.AdLookup",
      "callers": ["Foo.Users.UserService", "Foo.Auth.LoginHandler"],
      "members": [ { "signature": "Foo.Directory.AdEntry FindUser(string sam)", "callSites": 7, "wireFriendly": true } ],
      "proposedInterface": "IAdLookup",
      "score": 0.92,
      "articulationPoint": true
    }
  ],
  "extraction": { "moveToProject": "Foo.Windows", "types": [...], "estimatedLoc": 530 }
}
```

`--format dot|html` renders the type graph with taint coloring and cut edges
highlighted, useful for the conversation "this is the 3% we can't port".

Diagnostics: `OFR4001` no seam found (taint reaches an entry point directly),
`OFR4002` seam member not wire-friendly (`Stream`, delegates, events,
`ref`/`out`, `IntPtr`, non-serializable types), `OFR4003` static members on
the boundary (need an instance wrapper).

LLM garnish (`--llm`): propose interface names and DTO names for seams whose
boundary type has an unhelpful name. Never changes the cut.

## `extract interface`

```
offramp extract interface --project P --type Foo.Directory.AdLookup [--name IAdLookup] [--members m1,m2,...|--from-seams seams.json#seam-1] [--di microsoft|autofac|none] [--apply]
```

- Generates `IAdLookup.cs` next to the type with the selected members (from
  the seam, or all public members), makes the type implement it, and rewrites
  callers identified by the seam to depend on the interface: constructor
  parameters typed as the interface when the caller already uses constructor
  injection; otherwise a `TODO` diagnostic `OFR4010` per caller that
  instantiates the concrete type directly (with `--rewrite-new` it introduces
  a factory/DI registration and replaces `new AdLookup()` with resolution).
- Emits a DI registration snippet for the chosen container.
- This command edits code; it is not a pure move and lives in its own PR.

## `remote`

Turn a seam interface into an HTTP boundary: a host that runs the unportable
implementation on Windows and a client that implements the interface by
calling the host.

```
offramp remote --interface Foo.Directory.IAdLookup --implementation Foo.Directory.AdLookup
               [--host-framework auto|net10-windows|net48] [--transport http-json|grpc]
               [--serializer stj|newtonsoft] [--host-dir src/Foo.Windows.Host] [--client-dir src/Foo.Remote]
               [--container] [--apply]
```

Generated pieces:
1. **Boundary audit** (`OFR4002` errors stop generation unless `--skip-member`
   is used per member): every parameter and return type must be a DTO-able
   type: primitives, strings, enums, `DateTime(Offset)`, `Guid`, arrays and
   `List<>`/`Dictionary<string,>` of the same, and POCOs with public
   settable properties of the same. `Task<T>` unwrapped. `CancellationToken`
   allowed and mapped to request cancellation.
2. **Contracts project** (`Foo.Remote.Contracts`, `netstandard2.0`): DTOs
   generated from the parameter/return types (with mapping code when the
   original types are not themselves DTO-able but are convertible), and the
   route table (one POST per member, `/{interface}/{member}`).
3. **Host project**: ASP.NET Core minimal API. `--host-framework auto`
   chooses `net10.0-windows` when the implementation's project compiles
   against it with `Microsoft.Windows.Compatibility` (checked by trial
   compilation, the same machinery as `move plan`); otherwise `net48` with
   `Microsoft.Owin.SelfHost` + Web API 2 and a note that this is the legacy
   fallback. Includes health endpoint, structured logging, and an
   `appsettings.json`.
4. **Client project** (`Foo.Remote`, matching the calling project's targets):
   a class implementing the interface with `HttpClient` (typed client,
   `IHttpClientFactory` registration), sync members implemented by blocking
   on async with a `OFR4020` warning per member recommending an async
   interface variant, which `--async-variant` generates as `IAdLookupAsync`.
   Error mapping: non-2xx → `RemoteInvocationException` with the server's
   problem details.
5. **Container assets** (`--container`): Dockerfile on
   `mcr.microsoft.com/dotnet/aspnet:10.0-nanoserver-ltsc2022` (or the
   `framework/aspnet:4.8` image for the net48 fallback), and a sample
   Kubernetes Deployment with `nodeSelector: kubernetes.io/os: windows`,
   resource requests, readiness probe on the health endpoint, and a Service.
6. **DI switch**: registration snippet choosing local vs remote implementation
   by configuration, so the boundary can be flipped per environment.

`--transport grpc` generates a `.proto` from the contracts and uses
`Grpc.AspNetCore` on the host and `Grpc.Net.Client` on the client; it is not
available for the `net48` host fallback (`OFR4030`).

Acceptance: on the `seams` fixture, `seams` finds the `IDirectoryLookup`
articulation point; `extract interface` compiles; `remote --host-framework net10-windows`
produces host, contracts, and client projects that build on all three OSes
(the host builds with `EnableWindowsTargeting=true`; running it is not tested
on non-Windows), and a round-trip test with the host running in-process
(`WebApplicationFactory`) on the windows runner.

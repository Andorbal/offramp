# Offramp.Analyzers

Roslyn analyzers and code fixes for migrating .NET Framework code to modern .NET. They are
the codemods of the [offramp](https://github.com/Andorbal/offramp) tool, packaged for the IDE
and `dotnet format`.

| ID | Name | Rewrite |
|---|---|---|
| OFRM001 | sqlclient | `System.Data.SqlClient` → `Microsoft.Data.SqlClient` |
| OFRM002 | config-manager | `ConfigurationManager.AppSettings["k"]` → injected `IConfiguration["k"]` |
| OFRM003 | http-context | `HttpContext.Current` → injected `IHttpContextAccessor` (ASP.NET Core projects) |
| OFRM004 | webclient | `WebClient` downloads in async methods → `HttpClient` |
| OFRM005 | javascript-serializer | `JavaScriptSerializer` → `System.Text.Json` |
| OFRM006 | binaryformatter-clone | BinaryFormatter deep clone → System.Text.Json round trip |
| OFRM007 | thread-abort | `Thread.Abort` → cancellation (experimental) |
| OFRM008 | process-start-url | `Process.Start(file)` → `UseShellExecute = true` |
| OFRM009 | string-comparison | culture-sensitive comparisons → ordinal (off by default) |
| OFRM010 | codepages | registers `CodePagesEncodingProvider` at the entry point |
| OFRM011 | timezone-ids | `FindSystemTimeZoneById` → `TZConvert.GetTimeZoneInfo` |
| OFRM012 | service-controller | reports `ServiceController` use (needs a package) |
| OFRM013 | assemblyinfo | removes assembly attributes the SDK generates |

All rules are suggestions by default; `sample.editorconfig` in the package lists them.
Apply one across a project with `dotnet format analyzers --diagnostics OFRM008 --severity info`.

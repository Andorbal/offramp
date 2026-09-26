# behavior

One class per audit rule, named after it (`OFR3101`, `OFR3202`, ...), with members
named `Positive*` that must produce the rule's finding and members named `Negative*`
that must not (`AuditRunnerTests` checks both for every rule in every pack).

- `Behavior.Legacy`: `net48` library referencing `System.Web`, `System.ServiceModel`,
  `System.Activities`, `System.Runtime.Remoting`, `System.EnterpriseServices`, and the
  other assemblies the rules name, plus `Microsoft.Web.Infrastructure` (a package with
  only .NET Framework assets, left out of the target compilation: OFR3011).
  - `Rules/Api.cs`, `Rules/Behavior.cs`, `Rules/Serialization.cs`, `Rules/Native.cs`:
    one file per audit.
  - `app.config`: `<gcServer>` and `<gcConcurrent>` (OFR3116).
- `Behavior.Clean`: `net48` library that no rule matches, with an `app.config` that
  has no runtime settings: the negative project.

Exercised by: `audit api`, `audit behavior`, `audit serialization`, `audit native`,
`ifdef wrap` (statement and member wraps).

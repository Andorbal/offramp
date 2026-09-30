# 0052. Give trial compilations the .NET Standard facades a build adds

- Status: accepted
- Date: 2026-09-30
- Spec section: `docs/spec/commands/move.md#move-plan`

## Context

`move plan` proves a move with trial compilations built from the recorded ones: the destination with
the moved files and the references the move adds, and the source without them, referencing the
destination. When a .NET Framework project first references a .NET Standard assembly, MSBuild adds
facades to its compilation that it did not have before: `ImplicitlyExpandNETStandardFacades` (the
SDK's `Microsoft.NET.Build.Extensions`, with `netstandard.dll`, for net461 to net471) and
`ImplicitlyExpandDesignTimeFacades` (the reference assemblies' `Facades` folder, with
`netstandard.dll` from net471 on). The recorded compilation of a project that referenced no .NET
Standard assembly has none of them. In Open Live Writer 0.6.3, `move extract` from the net461
`OpenLiveWriter.CoreServices` into a new `netstandard2.0` project planned 0 moves: 11 × `OFR2104`,
CS0012 "The type 'Exception' is defined in an assembly that is not referenced ... 'netstandard,
Version=2.0.0.0'". Every first move out of a .NET Framework project that references no .NET
Standard project failed the same way. The specification says the trial compilation uses the recorded
compilation; it does not say where these facades come from, since the build that would add them has
not happened.

## Decision

When a trial compilation targets .NET Framework 4.6.1 or later, has no `netstandard` reference, and
gains a reference to an assembly that references `netstandard` or `System.Runtime`, the planner adds
the facades MSBuild would add, one file per assembly name, never replacing a reference the
compilation already has:

- for net461 to net471, the SDK's `Microsoft.NET.Build.Extensions/<tfm>/lib/*.dll`, with the folders
  its `Microsoft.NET.Build.Extensions.NETFramework.targets` picks for the version. The SDK is the
  newest one installed with the .NET that runs Offramp; those files have not changed since .NET Core
  2.0.
- then the `Facades` folder of the target's reference assemblies: the
  `Microsoft.NETFramework.ReferenceAssemblies.<tfm>` package the SDK restores (newest version in the
  global packages folder), else the targeting pack under `Program Files (x86)`. The compiler log
  cannot say which folder the build used: it records references by file name only.

When neither can be found the compilation stays as recorded, and the move reports what the
compiler reports.

## Alternatives considered

- Resolve the references of a scratch project with the real toolchain, as `move extract` does for a
  target the source does not compile for. It is the most faithful, but it runs MSBuild and a restore
  inside every plan for a set of files that is fixed per target.
- Synthesize a `netstandard` assembly forwarding each type to the recorded references. It needs no
  files, but it cannot forward what a net461 framework lacks (`System.ValueTuple` and the other
  assemblies the SDK's folder carries), and it is a type checker of our own.
- Report the missing facades instead of adding them. The move is valid and the build would pass;
  refusing it would keep the tool from the first step of every .NET Framework migration.

## Consequences

Moves out of (or into) .NET Framework projects that reference no .NET Standard assembly plan like
any other. The planner reads files from the SDK and the global packages folder; a machine with
neither (an unusual setup for a tool that builds with the SDK) plans as before. Verification by
`move apply` still builds with the real toolchain, so a facade set that differed from the build's
would show there.

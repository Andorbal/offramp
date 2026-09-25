namespace Offramp.Core.Diagnostics;

public static partial class DiagnosticCatalog
{
    private const string WorkspaceArea = "workspace";
    private const string EnvironmentArea = "environment";
    private const string ConfigArea = "configuration";
    private const string CliArea = "cli";

    public static readonly DiagnosticDescriptor OFR0001 = new(
        "OFR0001", Severity.Error,
        "workspace model missing",
        "The command needs the workspace model (`.offramp/workspace.json`) and it does not exist.",
        "`offramp scan` has not been run in this repository, or `--workspace` points somewhere else.",
        "Run `offramp scan`. `doctor` reports the same condition as a warning.",
        WorkspaceArea);

    public static readonly DiagnosticDescriptor OFR0010 = new(
        "OFR0010", Severity.Error,
        ".NET SDK not found",
        "`dotnet` could not be started, so nothing can be built, restored, or verified.",
        "No .NET SDK is installed, or `dotnet` is not on `PATH`.",
        "Install the .NET SDK for your `--target` from https://dot.net and make sure `dotnet --list-sdks` works.",
        EnvironmentArea);

    public static readonly DiagnosticDescriptor OFR0011 = new(
        "OFR0011", Severity.Error,
        "SDK requested by global.json is not installed",
        "`global.json` pins an SDK version that no installed SDK satisfies, so `dotnet` refuses to run in the repository.",
        "The pinned SDK was never installed on this machine, or `rollForward` is too strict.",
        "Install the SDK named in the message, or relax `sdk.rollForward` in `global.json`.",
        EnvironmentArea);

    public static readonly DiagnosticDescriptor OFR0012 = new(
        "OFR0012", Severity.Error,
        "selected SDK cannot target the requested framework",
        "The SDK that `dotnet` selects in this repository is older than the `--target` framework, so it cannot build `netN.0` projects.",
        "An older SDK is pinned in `global.json`, or only older SDKs are installed.",
        "Install the .NET SDK for the target and, if `global.json` pins an older one, update it.",
        EnvironmentArea);

    public static readonly DiagnosticDescriptor OFR0013 = new(
        "OFR0013", Severity.Error,
        ".NET Framework reference assemblies not resolvable",
        "`Microsoft.NETFramework.ReferenceAssemblies` is neither in the NuGet global packages folder nor available from any configured feed, so `net4x` targets cannot compile outside Windows.",
        "An offline machine with an empty package cache, or a `nuget.config` that removes nuget.org without a mirror of the package.",
        "Add a feed that carries `Microsoft.NETFramework.ReferenceAssemblies`, or restore once on a connected machine.",
        EnvironmentArea);

    public static readonly DiagnosticDescriptor OFR0014 = new(
        "OFR0014", Severity.Warning,
        "git not found",
        "`git` could not be started. Moves fall back to plain file moves, which git later sees as delete plus add unless it detects the rename.",
        "git is not installed or not on `PATH`.",
        "Install git so moves are staged with `git mv`.",
        EnvironmentArea);

    public static readonly DiagnosticDescriptor OFR0015 = new(
        "OFR0015", Severity.Warning,
        "not a git repository",
        "The repository root is not inside a git work tree, so moves are plain file moves and nothing is staged.",
        "Offramp was run outside a clone, or in an exported source tree.",
        "Run Offramp inside a git work tree to get staged, reviewable renames.",
        EnvironmentArea);

    public static readonly DiagnosticDescriptor OFR0016 = new(
        "OFR0016", Severity.Info,
        "no offramp.yml; built-in defaults in effect",
        "No configuration file was found at the repository root, so every setting has its built-in default.",
        "`offramp init` has not been run.",
        "Run `offramp init` to write `offramp.yml` with detected values.",
        ConfigArea);

    public static readonly DiagnosticDescriptor OFR0020 = new(
        "OFR0020", Severity.Error,
        "more than one solution found",
        "The repository contains several solution files and none was chosen, so Offramp cannot tell which one to work on.",
        "A repository with several `.sln`, `.slnx`, or `.slnf` files and no `solution:` in `offramp.yml`.",
        "Pass `--solution PATH` or set `solution:` in `offramp.yml`. `init` reports this as a warning and leaves `solution:` empty.",
        WorkspaceArea);

    public static readonly DiagnosticDescriptor OFR0030 = new(
        "OFR0030", Severity.Error,
        "offramp.yml already exists",
        "`init` did not write the configuration because the file already exists.",
        "`init` was run twice.",
        "Edit the existing file, or re-run with `--force` to replace it.",
        ConfigArea);

    public static readonly DiagnosticDescriptor OFR0050 = new(
        "OFR0050", Severity.Warning,
        "unknown key in offramp.yml",
        "A key in `offramp.yml` is not part of the configuration schema and is ignored.",
        "A typo (`verfiy:`), a key at the wrong nesting level, or a key from a newer Offramp version.",
        "Fix or remove the key. `schemas/v1/config.json` lists every valid key.",
        ConfigArea);

    public static readonly DiagnosticDescriptor OFR0051 = new(
        "OFR0051", Severity.Warning,
        "pin without a reason",
        "A `deps.pins` entry has no `reason`, so nobody can tell later why the version is held back.",
        "A pin added without documentation.",
        "Add `reason:` to the pin.",
        ConfigArea);

    public static readonly DiagnosticDescriptor OFR0052 = new(
        "OFR0052", Severity.Info,
        "rule override without a reason",
        "A `rules:` severity override has no `reason`, so the suppression is not attributable.",
        "An override added without documentation.",
        "Add `reason:` to the override.",
        ConfigArea);

    public static readonly DiagnosticDescriptor OFR0053 = new(
        "OFR0053", Severity.Error,
        "invalid value in offramp.yml",
        "A value in `offramp.yml` has the wrong type or is not one of the allowed values. The command stops because the configuration is ambiguous.",
        "For example `target: ten`, or `verify: { mode: compile }`.",
        "Correct the value; the message names the allowed values. `offramp doctor` lists every problem at once.",
        ConfigArea);

    public static readonly DiagnosticDescriptor OFR0054 = new(
        "OFR0054", Severity.Error,
        "offramp.yml is not valid YAML",
        "The configuration file could not be parsed.",
        "A YAML syntax error such as bad indentation or an unclosed quote.",
        "Fix the syntax at the reported line and column.",
        ConfigArea);

    public static readonly DiagnosticDescriptor OFR0055 = new(
        "OFR0055", Severity.Error,
        "configuration file not found",
        "The configuration file named by `--config` or `OFFRAMP_CONFIG` does not exist.",
        "A wrong path, or a path relative to a different working directory.",
        "Pass an existing file, or drop the option to use `offramp.yml` at the repository root.",
        ConfigArea);

    public static readonly DiagnosticDescriptor OFR0056 = new(
        "OFR0056", Severity.Error,
        "invalid configuration value from the environment",
        "An `OFFRAMP_*` environment variable has a value that does not fit the setting it maps to.",
        "For example `OFFRAMP_TARGET=ten` or `OFFRAMP_VERIFY__MODE=compile`.",
        "Correct or unset the environment variable named in the message.",
        ConfigArea);

    public static readonly DiagnosticDescriptor OFR0099 = new(
        "OFR0099", Severity.Error,
        "internal error",
        "Offramp hit an unexpected exception. This is a bug in Offramp, not in your repository.",
        "A defect in Offramp.",
        "Re-run with `--verbose` for the stack trace and report it at https://github.com/Andorbal/offramp/issues.",
        CliArea);
}

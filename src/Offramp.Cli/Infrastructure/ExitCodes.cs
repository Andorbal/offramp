namespace Offramp.Cli.Infrastructure;

/// <summary>Process exit codes (<c>docs/spec/01-cli-conventions.md#exit-codes</c>).</summary>
public static class ExitCodes
{
    /// <summary>Success, no diagnostics at or above <c>--fail-on</c>.</summary>
    public const int Success = 0;

    /// <summary>Completed with findings at or above <c>--fail-on</c>.</summary>
    public const int Findings = 1;

    /// <summary>Bad options, a missing required argument, or invalid configuration.</summary>
    public const int Usage = 2;

    /// <summary>The environment is missing something (SDK, workspace model, readable log, git when required), or an internal error.</summary>
    public const int Environment = 3;

    /// <summary>Some operations applied, some skipped.</summary>
    public const int Partial = 4;

    /// <summary>Interrupted (Ctrl-C).</summary>
    public const int Interrupted = 130;
}

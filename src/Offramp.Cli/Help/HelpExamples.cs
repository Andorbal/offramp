using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.Runtime.CompilerServices;

namespace Offramp.Cli;

/// <summary>Examples appended to <c>--help</c> for each command.</summary>
public static class HelpExamples
{
    private static readonly ConditionalWeakTable<Command, string[]> Examples = [];

    public static void Add(Command command, params string[] examples) => Examples.AddOrUpdate(command, examples);

    public static IReadOnlyList<string> For(Command command) =>
        Examples.TryGetValue(command, out var examples) ? examples : [];

    /// <summary>Wraps the default help action so it prints an "Examples:" section afterwards.</summary>
    public static void Install(RootCommand root, int width)
    {
        var help = root.Options.OfType<HelpOption>().Single();
        if (help.Action is not HelpAction action)
        {
            return;
        }

        action.MaxWidth = width;
        help.Action = new WithExamples(action);
    }

    private sealed class WithExamples(HelpAction inner) : SynchronousCommandLineAction
    {
        public override bool ClearsParseErrors => true;

        public override int Invoke(ParseResult parseResult)
        {
            var result = inner.Invoke(parseResult);
            var examples = For(parseResult.CommandResult.Command);
            if (examples.Count > 0)
            {
                var output = parseResult.InvocationConfiguration.Output;
                output.WriteLine("Examples:");
                foreach (var example in examples)
                {
                    output.WriteLine("  " + example);
                }

                output.WriteLine();
            }

            return result;
        }
    }
}

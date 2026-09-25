using System.Text;
using Offramp.Cli;
using Offramp.Cli.Infrastructure;

try
{
    // JSON and paths must survive redirection on Windows code pages.
    Console.OutputEncoding = new UTF8Encoding(false);
}
catch (IOException)
{
}

return await OfframpCli.RunAsync(args, CliHost.FromProcess());

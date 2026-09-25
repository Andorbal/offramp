using System.Runtime.CompilerServices;
using Offramp.Fixtures;

namespace Offramp.Cli.Tests;

internal static class ModuleInit
{
    [ModuleInitializer]
    internal static void Init() => Snapshots.Initialize();
}

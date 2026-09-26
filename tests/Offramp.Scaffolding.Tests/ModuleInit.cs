using System.Runtime.CompilerServices;
using Offramp.Fixtures;

namespace Offramp.Scaffolding.Tests;

internal static class ModuleInit
{
    [ModuleInitializer]
    internal static void Init() => Snapshots.Initialize();
}

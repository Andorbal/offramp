using System.Runtime.CompilerServices;
using Offramp.Fixtures;

namespace Offramp.Analysis.Tests;

internal static class ModuleInit
{
    [ModuleInitializer]
    internal static void Init() => Snapshots.Initialize();
}

using System.Runtime.CompilerServices;
using Offramp.Fixtures;

namespace Offramp.NuGet.Tests;

internal static class ModuleInit
{
    [ModuleInitializer]
    internal static void Init() => Snapshots.Initialize();
}

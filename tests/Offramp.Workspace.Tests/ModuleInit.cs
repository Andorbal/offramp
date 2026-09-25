using System.Runtime.CompilerServices;
using Offramp.Fixtures;

namespace Offramp.Workspace.Tests;

internal static class ModuleInit
{
    [ModuleInitializer]
    internal static void Init() => Snapshots.Initialize();
}

using System.Runtime.CompilerServices;
using Agwinterm.Core;

namespace Agwinterm.Tests;

internal static class GhosttyTestRuntime
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!GhosttyTerminalCore.TryLoad(AppContext.BaseDirectory, out string? error))
            throw new InvalidOperationException("Cannot load the bundled Ghostty test runtime: " + error);
    }
}

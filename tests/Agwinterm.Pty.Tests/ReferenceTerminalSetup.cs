using System.Runtime.CompilerServices;
using Agwinterm.Core;

namespace Agwinterm.Pty.Tests;

internal static class ReferenceTerminalSetup
{
    [ModuleInitializer]
    internal static void Initialize() =>
        TerminalSession.CoreFactory = (cols, rows) => new TerminalEmulator(cols, rows);
}
using Agwinterm.Pty;

namespace Agwinterm.Win32;

internal partial class Program
{
    private static StartupJournal? _startupJournal;
    private string? _startupProgress;
    private IReadOnlySet<string>? _restoreHostedIds;
}

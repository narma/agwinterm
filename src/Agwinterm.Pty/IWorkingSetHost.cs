using System.Text.Json;

namespace Agwinterm.Pty;

public interface IWorkingSetHost
{
    object WorkingSetState();
    object WorkingSetCommand(string command, JsonElement args, string? target);
}

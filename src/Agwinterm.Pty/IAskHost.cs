using Agwinterm.Core;

namespace Agwinterm.Pty;

public interface IAskHost
{
    string OpenAsk(AskSpec spec, string? window, bool follow, string? session = null);
    AskOutcome ReadAsk(string id, string? window);
    void CancelAsk(string id, string? window);
}

using System.Text.Json;

namespace Agwinterm.Pty;

public sealed class AgtermPresentationSender
{
    private readonly Action<string, JsonElement> _send;
    internal AgtermPresentationSender(Action<string, JsonElement> send) => _send = send;
    public string Mode { get; internal set; } = "mirror";
    public IReadOnlySet<string> Kinds { get; internal set; } = new HashSet<string>();
    public bool Granted { get; internal set; }
    public void Send(string kind, JsonElement body = default)
    {
        if (kind is not ("presenter.acquire" or "ask.resolve" or "ask.rejected" or "overlay.rejected" or "overlay.closed" or "page.rejected"))
            throw new IOException("Unsupported presentation response");
        _send(kind, body);
    }
}

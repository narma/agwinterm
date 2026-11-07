using Agwinterm.Core;

internal sealed class HarnessEffects : IHostActions
{
    public System.Collections.Concurrent.ConcurrentQueue<string> Events { get; } = new();
    public (Color Foreground,Color Background)? DefaultColors => null;
    public void Notify(string title,string body) => Events.Enqueue("notification:"+title+":"+body);
    public void Progress(int state,int value) => Events.Enqueue($"progress:{state}:{value}");
    public void ClipboardWrite(string text) => Events.Enqueue("clipboard:"+text);
    public ClipboardResult ClipboardExchange(ClipboardRequest request)
    {
        if (request.Read || request.Location != 0) return ClipboardResult.Denied;
        var text = request.Contents.FirstOrDefault(c=>c.Mime == "text/plain");
        if (text is null) return ClipboardResult.Unsupported;
        ClipboardWrite(System.Text.Encoding.UTF8.GetString(text.Data));
        return new(0);
    }
    public void Respond(string reply) => Events.Enqueue("response:"+reply);
    public void Bell() => Events.Enqueue("bell");
    public void CommandFinished(string token,long sequence) => Events.Enqueue($"completion:{sequence}");
    public void Unhandled(string kind,string detail) { }
}

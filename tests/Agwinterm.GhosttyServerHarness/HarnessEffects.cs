using Agwinterm.Core;

internal sealed class HarnessEffects : IHostActions
{
    public System.Collections.Concurrent.ConcurrentQueue<string> Events { get; } = new();
    public (Color Foreground,Color Background)? DefaultColors => null;
    public void Notify(string title,string body) => Events.Enqueue("notification:"+title+":"+body);
    public void Progress(int state,int value) => Events.Enqueue($"progress:{state}:{value}");
    public void ClipboardWrite(string text) => Events.Enqueue("clipboard:"+text);
    public void Respond(string reply) => Events.Enqueue("response:"+reply);
    public void Bell() => Events.Enqueue("bell");
    public void CommandFinished(string token,long sequence) => Events.Enqueue($"completion:{sequence}");
    public void Unhandled(string kind,string detail) { }
}

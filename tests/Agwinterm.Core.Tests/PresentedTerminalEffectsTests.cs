using Agwinterm.Core;
using Xunit;

namespace Agwinterm.Core.Tests;

public sealed class PresentedTerminalEffectsTests
{
    [Fact]
    public void RoleNoticeArrivingBeforePaneConfigurationIsRetained()
    {
        using var view=new PresentedTerminalView(10,2);
        view.PublishRoleNotice("zmx-role;initial");
        view.PublishRoleNotice("zmx-role;leader");
        var notices=new List<string>();
        view.SetTitleFilter(title=> { notices.Add(title); return true; });
        Assert.Equal(new[] { "zmx-role;leader" },notices);
        view.PublishRoleNotice("zmx-role;follower");
        Assert.Equal(new[] { "zmx-role;leader","zmx-role;follower" },notices);
    }
    private sealed class Actions : IHostActions
    {
        public readonly List<string> Events=[];
        public (Color Foreground,Color Background)? DefaultColors=>null;
        public void Notify(string title,string body)=>Events.Add(title+":"+body);
        public void Progress(int state,int value)=>Events.Add($"progress:{state}:{value}");
        public void ClipboardWrite(string text)=>Events.Add(text);
        public void Bell()=>Events.Add("bell");
        public void Respond(string reply)=>throw new InvalidOperationException("UI must not answer terminal queries");
        public void CommandFinished(string token,long sequence)=>Events.Add(token);
        public void Unhandled(string kind,string detail) { }
    }

    [Fact]
    public void StartupEffectsDrainOnceWhenPaneConnects()
    {
        using var view=new PresentedTerminalView(10,2);
        view.DispatchHostEffect(a=>a.Bell(),128);
        view.DispatchHostEffect(a=>a.ClipboardWrite("copied"),140);
        view.SetProgress(1,25);
        var first=new Actions(); view.Host=first;
        Assert.Equal(new[] { "progress:1:25","bell","copied" },first.Events);
        var replacement=new Actions(); view.Host=replacement;
        Assert.Equal(new[] { "progress:1:25" },replacement.Events);
    }

    [Fact]
    public void MissingPaneCannotAccumulateUnboundedEffects()
    {
        using var view=new PresentedTerminalView(10,2);
        view.DispatchHostEffect(a=>a.Bell(),1024*1024);
        Assert.Throws<InvalidDataException>(()=>view.DispatchHostEffect(a=>a.Bell(),1));
    }
}

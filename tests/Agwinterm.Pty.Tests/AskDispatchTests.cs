using System.Text.Json;
using Agwinterm.Core;

namespace Agwinterm.Pty.Tests;

public class AskDispatchTests
{
    private sealed class Host : IWindowHost, IAskHost
    {
        public int Calls; public string? Window; public bool Follow; public AskSpec? Spec;
        public string OpenAsk(AskSpec spec,string? window,bool follow) {Calls++;Spec=spec;Window=window;Follow=follow;return "ask-id";}
        public AskOutcome ReadAsk(string id,string? window) {Calls++;Window=window;return new("pending");}
        public void CancelAsk(string id,string? window) {Calls++;Window=window;}
        public ISessionHost? ResolveWindow(string? selector) => throw new Exception("picker must not resolve a session host");
        public IReadOnlyList<WindowSnapshot> Windows()=>[];
        public string WindowNew(string? n)=>throw new NotSupportedException();
        public bool WindowSelect(string? s)=>false; public bool WindowClose(string? s)=>false;
        public bool WindowDelete(string? s)=>false; public bool WindowRename(string? s,string n)=>false;
        public bool WindowResize(string? s,int w,int h)=>false; public bool WindowMove(string? s,int x,int y)=>false; public bool WindowZoom(string? s)=>false;
    }
    private static JsonElement Call(Host host,string raw)
    { using var server=new ControlServer(new FakeSessionHost(),host);using var doc=JsonDocument.Parse(server.Dispatch(raw));return doc.RootElement.Clone(); }

    [Fact] public void PickerRoutesBeforeSessionResolutionAndPreservesTypedOptions()
    {
        var host=new Host();var result=Call(host,"{\"cmd\":\"ask.open\",\"window\":\"w1\",\"args\":{\"form\":{},\"follow\":true}}");
        Assert.True(result.GetProperty("ok").GetBoolean());Assert.Equal("ask-id",result.GetProperty("result").GetProperty("id").GetString());
        Assert.Equal("w1",host.Window);Assert.True(host.Follow);Assert.Empty(host.Spec!.Fields);
        result=Call(host,"{\"cmd\":\"ask.result\",\"target\":\"ask-id\"}");
        Assert.Equal("pending",result.GetProperty("result").GetProperty("ask").GetProperty("result").GetString());Assert.Null(host.Window);
        Assert.True(Call(host,"{\"cmd\":\"ask.cancel\",\"target\":\"ask-id\"}").GetProperty("ok").GetBoolean());
    }

    [Theory]
    [InlineData("{\"cmd\":\"ask.result\"}")][InlineData("{\"cmd\":\"ask.cancel\",\"target\":3}")]
    [InlineData("{\"cmd\":\"ask.result\",\"target\":\"id\",\"window\":\"\"}")]
    [InlineData("{\"cmd\":\"ask.open\",\"target\":\"active\",\"args\":{\"form\":{}}}")]
    [InlineData("{\"cmd\":\"ask.open\",\"window\":false,\"args\":{\"form\":{}}}")]
    [InlineData("{\"cmd\":\"ask.open\",\"args\":{\"form\":{},\"follow\":\"true\"}}")]
    public void InvalidRequestNeverCallsHost(string raw)
    {var host=new Host();Assert.False(Call(host,raw).GetProperty("ok").GetBoolean());Assert.Equal(0,host.Calls);}
}

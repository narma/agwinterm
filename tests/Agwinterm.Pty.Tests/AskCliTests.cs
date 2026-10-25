using System.Text;
using System.Text.Json;
using Agwinterm.Ctl;

namespace Agwinterm.Pty.Tests;

public class AskCliTests
{
    private static JsonElement Json(string s) { using var d = JsonDocument.Parse(s); return d.RootElement.Clone(); }
    private static JsonElement Opened => Json("""{"ok":true,"result":{"id":"ask-1"}}""");
    private static JsonElement Pending => Json("""{"ok":true,"result":{"ask":{"result":"pending"}}}""");
    [Theory][InlineData("{\"bad\":true}")][InlineData("{")][InlineData("[]")]
    public void InvalidFormsNeverConnect(string form) => Assert.Equal(2, AskCli.Execute(["ask"], Encoding.UTF8.GetBytes(form), _ => throw new Exception("connected"), _ => {}, _ => {}, _ => {}));
    [Fact] public void KnownIdCancellationPinsIdentityAndReturns130()
    {
        using var stop = new CancellationTokenSource(); var calls = new List<JsonElement>();
        int code = AskCli.Execute(["ask", "--window", "active"], "{}"u8.ToArray(), raw =>
        { calls.Add(Json(raw)); return calls.Count == 1 ? Opened : Pending; }, _ => stop.Cancel(), _ => {}, _ => {}, stop.Token);
        Assert.Equal(130, code); Assert.Equal(3, calls.Count);
        Assert.Equal("ask.cancel", calls[^1].GetProperty("cmd").GetString());
        Assert.All(calls.Skip(1), c => { Assert.Equal("ask-1", c.GetProperty("target").GetString()); Assert.False(c.TryGetProperty("window", out _)); });
    }
    [Fact] public void LostOpenReplyDoesNotRetryOrCancelUnknownId()
    {
        int calls = 0;
        Assert.Equal(1, AskCli.Execute(["ask"], "{}"u8.ToArray(), _ => { calls++; throw new IOException("lost"); }, _ => {}, _ => {}, _ => {})); Assert.Equal(1, calls);
    }
    [Fact] public void NoBlockLeavesOwnershipWithTheForm()
    {
        int calls = 0; var output = new List<string>();
        Assert.Equal(0, AskCli.Execute(["ask", "--no-block"], "{}"u8.ToArray(), _ => { calls++; return Opened; }, _ => Assert.Fail("wait"), output.Add, Assert.Fail));
        Assert.Equal(1, calls); Assert.Equal("ask-1", Json(Assert.Single(output)).GetProperty("id").GetString());
    }
    [Theory][InlineData("pending", null, 1)][InlineData("cancelled", "owner-closed", 2)][InlineData("answered", null, 0)]
    public void ResultExitMatchesOutcome(string result, string? reason, int expected)
    {
        string reply = JsonSerializer.Serialize(new { ok = true, result = new { ask = new { result, reason, button = "submit", values = new { } } } });
        Assert.Equal(expected, AskCli.Execute(["ask", "result", "exact"], [], _ => Json(reply), _ => {}, _ => {}, Assert.Fail));
    }
    [Fact] public void PickMetadataSurvivesCliSerialization()
    {
        var input = """[{"id":"a","label":"same","status":"ready","color":"#123456","shape":"square"},{"id":"","label":"same"}]""";
        Assert.Equal(0, PickCli.Execute(["pick", "--selected-id", "", "--no-block"], Encoding.UTF8.GetBytes(input), raw =>
        {
            var a = Json(raw).GetProperty("args"); Assert.Equal("", a.GetProperty("selectedId").GetString());
            Assert.Equal("square", a.GetProperty("items")[0].GetProperty("shape").GetString()); return Opened;
        }, _ => {}, _ => {}, Assert.Fail));
    }
}

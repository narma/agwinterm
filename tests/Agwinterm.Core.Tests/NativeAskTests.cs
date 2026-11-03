using System.Text.Json;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class NativeAskTests
{
    private static AskSpec Parse(string json) { using var d = JsonDocument.Parse(json); return AskSpec.Parse(d.RootElement); }
    [Theory]
    [InlineData("[]")][InlineData("{\"unknown\":true}")][InlineData("{\"title\":\"a\",\"title\":\"b\"}")]
    [InlineData("{\"buttons\":[]}")][InlineData("{\"fields\":[{\"id\":\"x\",\"label\":\"x\",\"type\":\"html\"}]}")]
    [InlineData("{\"fields\":[{\"id\":\"x\",\"label\":\"x\",\"type\":\"single\",\"choices\":[]}]}")]
    [InlineData("{\"fields\":[{\"id\":\"x\",\"label\":\"x\",\"type\":\"text\",\"allowOther\":false}]}")]
    [InlineData("{\"buttons\":[{\"id\":\"a\",\"label\":\"a\",\"hotkey\":\"д\"},{\"id\":\"b\",\"label\":\"b\",\"hotkey\":\"Д\"}]}")]
    public void MalformedFormsRefuse(string json) => Assert.Throws<ArgumentException>(() => Parse(json));

    [Fact] public void AnswersKeepTypedDataAndInputChoiceOrder()
    {
        var spec = Parse("""{"fields":[{"id":"text","label":"Имя","type":"multiline"},{"id":"choice","label":"Выбор","type":"multi","allowOther":true,"choices":[{"id":"a","label":"same"},{"id":"b","label":"same"}]}]}""");
        var values = new Dictionary<string, AskValue> { ["text"] = new("hello\n$(not executed)", [], null), ["choice"] = new("", ["b", "a"], "other") };
        var outcome = AskOutcome.Answer(spec, "submit", values);
        using var d = JsonDocument.Parse(JsonSerializer.Serialize(outcome.Wire()));
        Assert.Equal("hello\n$(not executed)", d.RootElement.GetProperty("values").GetProperty("text").GetString());
        Assert.Equal("a", d.RootElement.GetProperty("values").GetProperty("choice").GetProperty("selected")[0].GetString());
        values["choice"] = new("", ["unknown"], null);
        Assert.Throws<ArgumentException>(() => AskOutcome.Answer(spec, "submit", values));
    }
    [Fact] public void FirstCompletionAndOwnerClosureRemainStable()
    {
        var r = new AskRegistry(); var spec = Parse("{}"); var p = r.Open("w", spec)!;
        Assert.Null(r.Open("w", spec)); Assert.True(r.Resolve(p.Id, AskOutcome.Answer(spec, "submit", new Dictionary<string, AskValue>())));
        Assert.False(r.Resolve(p.Id, new("cancelled", "cancel"))); Assert.Equal("answered", r.Find(p.Id)!.Outcome.Result);
        var next = r.Open("w", spec)!; r.CloseWindow("w");
        Assert.Equal("owner-closed", r.Find(next.Id)!.Outcome.Reason); Assert.Equal("answered", r.Find(p.Id)!.Outcome.Result);
        Assert.Null(r.Find(next.Id[..8]));
    }
    [Fact] public void RetentionUsesBoundedCompletionOrder()
    {
        var r = new AskRegistry(); var spec = Parse("{}"); var ids = new List<string>();
        for (int i = 0; i < 9; i++) { var p = r.Open("w", spec)!; ids.Add(p.Id); r.Resolve(p.Id, new("cancelled", "escape")); }
        Assert.Null(r.Find(ids[0])); Assert.NotNull(r.Find(ids[1]));
        r.CloseWindow("w");
        for (int i = 0; i < 32; i++) { var p = r.Open("w" + i, spec)!; r.CloseWindow(p.Window); }
        Assert.Null(r.Find(ids[^1]));
    }
    [Fact] public void SessionOwnershipIsImmutableAndClosureIsScoped()
    {
        var registry = new AskRegistry(); var spec = Parse("{}");
        var targeted = registry.Open("window-a", spec, "session-a")!;
        Assert.Equal("session-a", registry.Find(targeted.Id)!.Session);
        Assert.Null(registry.CloseSession("window-b", "session-a"));
        Assert.Null(registry.CloseSession("window-a", "session-b"));
        Assert.Equal("pending", registry.Find(targeted.Id)!.Outcome.Result);
        Assert.Equal(targeted.Id, registry.CloseSession("window-a", "session-a"));
        Assert.False(registry.Resolve(targeted.Id, AskOutcome.Answer(spec, "submit", new Dictionary<string, AskValue>())));
        registry.CloseWindow("window-a");
        var retained = registry.Find(targeted.Id)!;
        Assert.Equal("session-a", retained.Session); Assert.Equal("window-a", retained.Window);
        Assert.Equal("owner-closed", retained.Outcome.Reason);
        var legacy = registry.Open("window-a", spec)!;
        Assert.Null(registry.CloseSession("window-a", "session-a"));
        Assert.Null(registry.Find(legacy.Id)!.Session);
        Assert.Equal("pending", registry.Find(legacy.Id)!.Outcome.Result);
    }
    [Fact] public void CollectionAndTextLimitsRefuse()
    {
        var f = new { id = "x", label = "x", type = "text" };
        Assert.Throws<ArgumentException>(() => Parse(JsonSerializer.Serialize(new { fields = Enumerable.Repeat(f, 17) })));
        Assert.Throws<ArgumentException>(() => Parse(JsonSerializer.Serialize(new { title = new string('x', 4097) })));
        var spec = Parse("""{"fields":[{"id":"x","label":"x","type":"text"}]}""");
        Assert.Throws<ArgumentException>(() => AskOutcome.Answer(spec, "submit", new Dictionary<string, AskValue> { ["x"] = new(new string('x', 4097), [], null) }));
    }
}

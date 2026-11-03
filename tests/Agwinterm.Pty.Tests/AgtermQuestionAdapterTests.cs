using System.Text.Json;
using Agwinterm.Core;

namespace Agwinterm.Pty.Tests;

public class AgtermQuestionAdapterTests
{
    private static readonly HashSet<string> Kinds = ["ask.form", "ask.other"];
    private static AskSpec Spec(string json) => AskSpec.Parse(JsonSerializer.Deserialize<JsonElement>(json));

    [Fact] public void RealCapturedInputPreservesPrefillButtonsAndAuthoritativeIdentity()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Presentation", "pinned-frames.json")));
        var frame = doc.RootElement.EnumerateArray().Single(e => e.GetProperty("frame").GetProperty("kind").GetString() == "ask.request").GetProperty("frame");
        var question = AgtermQuestionAdapter.FromOrigin(frame.GetProperty("ask"), new Dictionary<Guid, string>(), Kinds);
        Assert.Equal("fixture", question.Spec.InitialValues!["remote"].Text);
        Assert.Equal("ok", question.Spec.DefaultButtonId); Assert.Equal("cancel", question.Spec.CancelButtonId);
        var outcome = AskOutcome.Answer(question.Spec, "ok", new Dictionary<string, AskValue> { ["remote"] = new("exact answer", [], null) });
        var answer = AgtermQuestionAdapter.Answer(question, outcome);
        Assert.Equal(question.Id, answer.GetProperty("id").GetString()); Assert.Equal(7, answer.GetProperty("owner").GetInt64());
        Assert.Equal("exact answer", answer.GetProperty("text").GetString());
        var cancel = AgtermQuestionAdapter.Answer(question, AskOutcome.Answer(question.Spec, "cancel", new Dictionary<string, AskValue>()));
        Assert.False(cancel.TryGetProperty("text", out _)); Assert.Equal("cancel", cancel.GetProperty("button").GetString());
    }

    [Theory] [InlineData("single")] [InlineData("multi")]
    public void ChoiceRoundtripKeepsNativeFieldAndChoiceIds(string type)
    {
        var native = Spec($$"""{"fields":[{"id":"original","label":"Pick","type":"{{type}}","allowOther":true,"choices":[{"id":"one","label":"Same"},{"id":"two","label":"Same"}]}]}""");
        var request = AgtermQuestionAdapter.ToOrigin("exact-id", 3, native, Kinds);
        var mapped = AgtermQuestionAdapter.FromOrigin(request, new Dictionary<Guid, string>(), Kinds);
        Assert.Equal(type == "single" ? new[] { "one" } : [], mapped.Spec.InitialValues!["remote"].Selected);
        var values = new Dictionary<string, AskValue> { ["remote"] = new("", ["two"], null) };
        var answer = AgtermQuestionAdapter.Answer(mapped, AskOutcome.Answer(mapped.Spec, "submit", values));
        var result = AgtermQuestionAdapter.Resolve(native, "exact-id", 3, answer);
        Assert.True(result.Values!.ContainsKey("original"));
        Assert.Equal("two", JsonSerializer.SerializeToElement(result.Values["original"]).GetProperty("selected")[0].GetString());
        values["remote"] = new("", [], "custom");
        answer = AgtermQuestionAdapter.Answer(mapped, AskOutcome.Answer(mapped.Spec, "submit", values));
        Assert.Equal("custom", JsonSerializer.SerializeToElement(AgtermQuestionAdapter.Resolve(native, "exact-id", 3, answer).Values!["original"]).GetProperty("other").GetString());
    }

    [Fact] public void DelegatedRadioPolicyDoesNotChangeLocalForms()
    {
        var spec = Spec("""{"fields":[{"id":"x","label":"Pick","type":"single","allowOther":true,"choices":[{"id":"a","label":"A"}]}]}""");
        var empty = new Dictionary<string, AskValue> { ["x"] = new("", [], null) };
        Assert.Equal("answered", AskOutcome.Answer(spec, "submit", empty).Result);
        Assert.Throws<ArgumentException>(() => AskOutcome.Answer(spec with { DelegatedChoices = true }, "submit", empty));
        var both = new Dictionary<string, AskValue> { ["x"] = new("", ["a"], "other") };
        Assert.Equal("answered", AskOutcome.Answer(spec, "submit", both).Result);
        Assert.Throws<ArgumentException>(() => AskOutcome.Answer(spec with { DelegatedChoices = true }, "submit", both));
    }

    [Fact] public void MultipleFieldsAndUnnegotiatedFormsAreRefusedBeforeOpening()
    {
        var spec = Spec("""{"fields":[{"id":"a","label":"A","type":"text"},{"id":"b","label":"B","type":"text"}]}""");
        Assert.Throws<IOException>(() => AgtermQuestionAdapter.ToOrigin("id", 1, spec, Kinds));
        spec = spec with { Fields = spec.Fields.Take(1).ToArray() };
        Assert.Throws<IOException>(() => AgtermQuestionAdapter.ToOrigin("id", 1, spec, new HashSet<string>()));
    }
    [Fact] public void NativeMultilineLineEndingsBecomeValidWireNewlines()
    {
        var spec = Spec("""{"fields":[{"id":"text","label":"Input","type":"multiline"}]}""");
        var question = new AgtermQuestion("id", 1, null, spec);
        var answer = AgtermQuestionAdapter.Answer(question, AskOutcome.Answer(spec, "submit",
            new Dictionary<string, AskValue> { ["text"] = new("a\r\nb\n", [], null) }));
        Assert.Equal("a\nb\n", answer.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("{\"id\":\"wrong\",\"owner\":1,\"button\":\"submit\",\"text\":\"a\"}")]
    [InlineData("{\"id\":\"id\",\"owner\":2,\"button\":\"submit\",\"text\":\"a\"}")]
    [InlineData("{\"id\":\"id\",\"owner\":1,\"button\":\"submit\",\"selected\":[]}")]
    [InlineData("{\"id\":\"id\",\"owner\":1,\"button\":\"other\",\"text\":\"a\"}")]
    public void StaleOrMalformedAnswersAreRefused(string raw)
    {
        var spec = Spec("""{"fields":[{"id":"a","label":"A","type":"text"}]}""");
        Assert.Throws<IOException>(() => AgtermQuestionAdapter.Resolve(spec, "id", 1, JsonSerializer.Deserialize<JsonElement>(raw)));
    }

    [Theory]
    [InlineData("{\"selected\":[\"missing\"]}")]
    [InlineData("{\"selected\":[\"a\",\"a\"]}")]
    [InlineData("{\"selected\":[]}")]
    [InlineData("{\"selected\":[\"a\"],\"text\":\"other\"}")]
    [InlineData("{\"selected\":[1]}")]
    public void InvalidRadioSelectionsNeverSettleAnOriginQuestion(string fields)
    {
        var spec = Spec("""{"fields":[{"id":"x","label":"Pick","type":"single","allowOther":true,"choices":[{"id":"a","label":"A"}]}]}""");
        var body = System.Text.Json.Nodes.JsonNode.Parse(fields)!.AsObject();
        body["id"] = "q"; body["owner"] = 1; body["button"] = "submit";
        Assert.Throws<IOException>(() => AgtermQuestionAdapter.Resolve(spec, "q", 1, JsonSerializer.SerializeToElement(body)));
    }

    [Theory]
    [InlineData("width", "40")]
    [InlineData("destructiveID", "\"submit\"")]
    [InlineData("pane", "\"scratch\"")]
    [InlineData("defaultID", "\"unknown\"")]
    [InlineData("owner", "0")]
    public void UnsupportedPresentationOrAuthorityRefusesBeforeUi(string field, string json)
    {
        var wire = AgtermQuestionAdapter.ToOrigin("q", 1, Spec("{}"), Kinds);
        var body = System.Text.Json.Nodes.JsonNode.Parse(wire.GetRawText())!.AsObject();
        body[field] = System.Text.Json.Nodes.JsonNode.Parse(json);
        Assert.Throws<IOException>(() => AgtermQuestionAdapter.FromOrigin(JsonSerializer.SerializeToElement(body), new Dictionary<Guid, string>(), Kinds));
    }

    [Fact]
    public void FormBoundaryAndAnswerLimitsRefuseWithoutTruncation()
    {
        var native = Spec(JsonSerializer.Serialize(new { fields = new[] { new { id = "x", label = "Pick", type = "single",
            choices = Enumerable.Range(0, 51).Select(i => new { id = i.ToString(), label = "Item" }).ToArray() } } }));
        Assert.Throws<IOException>(() => AgtermQuestionAdapter.ToOrigin("q", 1, native, Kinds));
        native = Spec("""{"fields":[{"id":"x","label":"Text","type":"text"}]}""");
        var answer = JsonSerializer.SerializeToElement(new { id = "q", owner = 1, button = "submit", text = new string('x', AskSpec.MaxText + 1) });
        Assert.Throws<IOException>(() => AgtermQuestionAdapter.Resolve(native, "q", 1, answer));
    }
}

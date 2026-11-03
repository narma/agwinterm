using System.Text.Json;
using Agwinterm.Core;

namespace Agwinterm.Pty;

public sealed record AgtermQuestion(string Id, long Owner, string? Pane, AskSpec Spec);

/// <summary>Lossless IDs and typed outcomes for the negotiated one-field question subset.</summary>
public static class AgtermQuestionAdapter
{
    private static JsonElement Optional(JsonElement body, string name) => body.TryGetProperty(name, out var value) ? value : default;
    private static bool Absent(JsonElement value) => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
    private static string? Text(JsonElement body, string name) => Absent(Optional(body, name)) ? null : body.GetProperty(name).GetString();
    public static AgtermQuestion FromOrigin(JsonElement body, IReadOnlyDictionary<Guid, string> mapping, IReadOnlySet<string> kinds)
    {
        try
        {
            string id = Text(body, "id") ?? throw new ArgumentException("Missing question ID");
            long owner = body.GetProperty("owner").GetInt64();
            if (id.Length is < 1 or > 256 || id.Any(char.IsControl) || owner < 1) throw new ArgumentException("Invalid question authority");
            string? pane = Text(body, "pane");
            if (pane is not null)
            {
                if (!Guid.TryParse(pane, out var origin) || !mapping.TryGetValue(origin, out pane)) throw new ArgumentException("Unknown question pane");
            }
            if (!Absent(Optional(body, "width")) || !Absent(Optional(body, "destructiveID")))
                throw new ArgumentException("Question width/destructive styling unsupported");
            if (Text(body, "style") is not ("gui" or "terminal") || Text(body, "align") is not ("left" or "center" or "right"))
                throw new ArgumentException("Invalid question presentation");
            var input = Optional(body, "input"); var choices = Optional(body, "choices");
            if (!Absent(input) && !Absent(choices)) throw new ArgumentException("Question has two forms");
            string? cancel = Text(body, "cancelID"), defaultId = Text(body, "defaultID");
            if ((!Absent(input) || !Absent(choices) || cancel is not null) && !kinds.Contains("ask.form")) throw new ArgumentException("Question forms not negotiated");
            var fields = new List<object>(); AskValue? initial = null;
            if (!Absent(input))
            {
                string mode = Text(input, "mode") switch { "line" => "text", "multiline" => "multiline", _ => throw new ArgumentException("Unknown input mode") };
                fields.Add(new { id = "remote", label = Text(input, "placeholder") is { Length: > 0 } label ? label : "Input", type = mode });
                initial = new(Text(input, "value") ?? "", [], null);
            }
            if (!Absent(choices))
            {
                bool multiple = choices.GetProperty("multiple").GetBoolean();
                string? other = Text(choices, "other");
                if (other is not null && !kinds.Contains("ask.other")) throw new ArgumentException("Other not negotiated");
                var list = choices.GetProperty("choices").EnumerateArray().ToArray();
                if (list.Length is < 1 or > 50 || list.Any(c => !Absent(Optional(c, "subtitle")))) throw new ArgumentException("Unsupported choice list");
                fields.Add(new { id = "remote", label = "Choice", type = multiple ? "multi" : "single", allowOther = other is not null,
                    choices = list.Select(c => new { id = c.GetProperty("id").GetString(), label = c.GetProperty("label").GetString() }).ToArray() });
                var selected = choices.GetProperty("checked").EnumerateArray().Select(v => v.GetString() ?? throw new ArgumentException("Invalid checked ID")).ToArray();
                if (!multiple && selected.Length == 0) selected = [list[0].GetProperty("id").GetString()!];
                initial = new("", selected, null);
            }
            var buttons = body.GetProperty("buttons");
            if (buttons.GetArrayLength() is < 1 or > 6) throw new ArgumentException("Unsupported button count");
            var spec = AskSpec.Parse(JsonSerializer.SerializeToElement(new { title = Text(body, "title") ?? "", prompt = Text(body, "message") ?? "", fields,
                buttons = buttons.EnumerateArray().Select(b =>
                {
                    var button = new Dictionary<string, object?> { ["id"] = b.GetProperty("id").GetString(), ["label"] = b.GetProperty("label").GetString() };
                    if (Text(b, "hotkey") is { } key) button["hotkey"] = key;
                    return button;
                }).ToArray() })) with { DefaultButtonId = defaultId, CancelButtonId = cancel, DelegatedChoices = true,
                    InitialValues = initial is null ? null : new Dictionary<string, AskValue> { ["remote"] = initial } };
            if (new[] { defaultId, cancel }.Any(role => role is not null && !spec.Buttons.Any(b => b.Id == role))) throw new ArgumentException("Unknown button role");
            // Validate prefill and checked IDs using exactly the answer validator, before opening any UI.
            if (initial is not null)
                _ = AskOutcome.Answer(spec with { CancelButtonId = null }, spec.Buttons[0].Id, spec.InitialValues!);
            return new(id, owner, pane, spec);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new IOException("Unsupported or malformed remote question", ex); }
    }

    public static JsonElement ToOrigin(string id, long owner, AskSpec spec, IReadOnlySet<string> kinds)
    {
        if (spec.Fields.Count > 1 || spec.Buttons.Count > 6 || spec.Buttons.Any(b => b.Hotkey is { } key && (key.Length != 1 || !char.IsAsciiLetter(key[0]))))
            throw new IOException("Native form cannot be represented by agterm");
        var field = spec.Fields.FirstOrDefault();
        if (field is not null && !kinds.Contains("ask.form") || field?.AllowOther == true && !kinds.Contains("ask.other"))
            throw new IOException("Question extensions unavailable");
        if (field?.Choices.Count > 50) throw new IOException("Too many choices for agterm");
        var result = new Dictionary<string, object?> { ["id"] = id, ["owner"] = owner, ["title"] = spec.Title,
            ["message"] = field is null ? spec.Prompt : spec.Prompt + (spec.Prompt.Length == 0 ? "" : "\n\n") + field.Label,
            ["buttons"] = spec.Buttons.Select(b => b.Hotkey is null ? (object)new { id = b.Id, label = b.Label } : new { id = b.Id, label = b.Label, hotkey = b.Hotkey }).ToArray(),
            ["defaultID"] = spec.Buttons[0].Id, ["style"] = "gui", ["align"] = "right" };
        if (field?.Type is "text" or "multiline") result["input"] = new { mode = field.Type == "text" ? "line" : "multiline", value = "" };
        if (field?.Type is "single" or "multi") result["choices"] = new { choices = field.Choices.Select(c => new { id = c.Id, label = c.Label }).ToArray(),
            multiple = field.Type == "multi", @checked = field.Type == "single" ? new[] { field.Choices[0].Id } : [], other = field.AllowOther ? "Other" : null };
        var payload = JsonSerializer.SerializeToElement(result);
        _ = AgtermPresentationProtocol.Encode(new("ask.request", long.MaxValue, long.MaxValue, payload));
        return payload;
    }

    public static JsonElement Answer(AgtermQuestion question, AskOutcome outcome)
    {
        var answer = new Dictionary<string, object?> { ["id"] = question.Id, ["owner"] = question.Owner };
        if (outcome.Result == "answered")
        {
            if (!question.Spec.Buttons.Any(b => b.Id == outcome.Button)) throw new IOException("Unknown answer button");
            answer["button"] = outcome.Button;
            if (outcome.Button != question.Spec.CancelButtonId && question.Spec.Fields.FirstOrDefault() is { } field)
            {
                if (outcome.Values?.TryGetValue(field.Id, out var value) != true) throw new IOException("Missing answer field");
                var json = JsonSerializer.SerializeToElement(value);
                if (field.Type is "text" or "multiline")
                {
                    string text = json.GetString()!;
                    // Native multiline edits use CRLF; agterm's form protocol permits LF, never CR.
                    if (field.Type == "multiline") text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
                    if (text.Contains('\r')) throw new IOException("Unrepresentable remote input newline");
                    answer["text"] = text;
                }
                else
                {
                    answer["selected"] = json.GetProperty("selected").EnumerateArray().Select(v => v.GetString()).ToArray();
                    if (Text(json, "other") is { } other && !string.IsNullOrWhiteSpace(other)) answer["text"] = other;
                }
            }
        }
        else if (outcome.Result != "cancelled") throw new IOException("Question has no terminal outcome");
        return JsonSerializer.SerializeToElement(answer);
    }

    public static AskOutcome Resolve(AskSpec spec, string id, long owner, JsonElement answer)
    {
        try
        {
            if (Text(answer, "id") != id || answer.GetProperty("owner").GetInt64() != owner) throw new ArgumentException("Stale question authority");
            string? button = Text(answer, "button");
            if (button is null)
            {
                if (!Absent(Optional(answer, "text")) || !Absent(Optional(answer, "selected"))) throw new ArgumentException("Dismissal carries answer fields");
                return new("cancelled", "escape");
            }
            if (button == spec.CancelButtonId) return AskOutcome.Answer(spec, button, new Dictionary<string, AskValue>());
            var values = new Dictionary<string, AskValue>();
            if (spec.Fields.Count > 1) throw new ArgumentException("Multi-field answer not supported");
            if (spec.Fields.FirstOrDefault() is { } field)
            {
                var selected = Optional(answer, "selected"); string? text = Text(answer, "text");
                if (field.Type is "text" or "multiline")
                {
                    if (!Absent(selected) || text is null) throw new ArgumentException("Incorrect text answer type");
                    values[field.Id] = new(text, [], null);
                }
                else
                {
                    if (selected.ValueKind != JsonValueKind.Array || text is not null && !field.AllowOther) throw new ArgumentException("Incorrect choice answer type");
                    values[field.Id] = new("", selected.EnumerateArray().Select(v => v.GetString() ?? throw new ArgumentException("Null choice")).ToArray(), text);
                }
            }
            else if (!Absent(Optional(answer, "text")) || !Absent(Optional(answer, "selected"))) throw new ArgumentException("Unexpected answer fields");
            return AskOutcome.Answer(spec with { DelegatedChoices = true }, button, values);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new IOException("Invalid remote answer", ex); }
    }
}

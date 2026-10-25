using System.Text;
using System.Text.Json;

namespace Agwinterm.Core;

public sealed record AskChoice(string Id, string Label);
public sealed record AskField(string Id, string Label, string Type, IReadOnlyList<AskChoice> Choices, bool AllowOther);
public sealed record AskButton(string Id, string Label, string? Hotkey);
public sealed record AskSpec(string Title, string Prompt, IReadOnlyList<AskField> Fields, IReadOnlyList<AskButton> Buttons)
{
    public const int MaxBytes = 1024 * 1024, MaxText = 4096, MaxMultiline = 16384;
    public static void Properties(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("ask requires an object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in value.EnumerateObject())
            if (!allowed.Contains(p.Name, StringComparer.Ordinal) || !seen.Add(p.Name))
                throw new ArgumentException("unknown or duplicate ask property: " + p.Name);
    }
    private static string Text(JsonElement value, string name, string? fallback = null)
    {
        if (!value.TryGetProperty(name, out var p)) return fallback ?? throw new ArgumentException("ask requires " + name);
        if (p.ValueKind != JsonValueKind.String || !PickSpec.DisplayText(p.GetString()!)) throw new ArgumentException("invalid ask " + name);
        string text = p.GetString()!;
        if (fallback is null && string.IsNullOrWhiteSpace(text)) throw new ArgumentException("empty ask " + name);
        return text;
    }
    private static JsonElement[] Array(JsonElement value, string name, int max, bool required = false)
    {
        if (!value.TryGetProperty(name, out var p))
        { if (required) throw new ArgumentException("ask requires " + name); return []; }
        if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() > max || (required && p.GetArrayLength() == 0))
            throw new ArgumentException("invalid ask " + name + " count (max " + max + ")");
        return p.EnumerateArray().ToArray();
    }
    public static AskSpec Parse(JsonElement form)
    {
        Properties(form, "title", "prompt", "fields", "buttons");
        if (Encoding.UTF8.GetByteCount(form.GetRawText()) > MaxBytes) throw new ArgumentException("ask form exceeds 1 MiB");
        var fields = new List<AskField>(); var ids = new HashSet<string>(StringComparer.Ordinal); int total = 0;
        foreach (var f in Array(form, "fields", 16))
        {
            Properties(f, "id", "label", "type", "choices", "allowOther");
            string id = Text(f, "id"), label = Text(f, "label"), type = Text(f, "type");
            if (!ids.Add(id)) throw new ArgumentException("duplicate ask field id");
            if (type is not ("text" or "multiline" or "single" or "multi")) throw new ArgumentException("invalid ask field type");
            bool choice = type is "single" or "multi";
            if (!choice && (f.TryGetProperty("choices", out _) || f.TryGetProperty("allowOther", out _)))
                throw new ArgumentException("choices/allowOther require a choice field");
            bool other = false;
            if (f.TryGetProperty("allowOther", out var o))
            {
                if (o.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("allowOther must be boolean");
                other = o.GetBoolean();
            }
            var choices = new List<AskChoice>(); var choiceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in Array(f, "choices", 64, choice))
            {
                Properties(c, "id", "label"); var cid = Text(c, "id");
                if (!choiceIds.Add(cid)) throw new ArgumentException("duplicate ask choice id");
                choices.Add(new(cid, Text(c, "label")));
            }
            total += choices.Count;
            if (total > 512) throw new ArgumentException("ask exceeds 512 choices");
            fields.Add(new(id, label, type, choices.AsReadOnly(), other));
        }
        var buttons = new List<AskButton>(); ids.Clear(); var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!form.TryGetProperty("buttons", out _)) buttons.Add(new("submit", "Submit", null));
        else foreach (var b in Array(form, "buttons", 8, true))
        {
            Properties(b, "id", "label", "hotkey"); var id = Text(b, "id");
            if (!ids.Add(id)) throw new ArgumentException("duplicate ask button id");
            string? key = b.TryGetProperty("hotkey", out _) ? Text(b, "hotkey") : null;
            if (key is not null && (key.Length != 1 || !char.IsLetterOrDigit(key[0]) || !keys.Add(key)))
                throw new ArgumentException("ask hotkeys must be unique single letters or digits");
            buttons.Add(new(id, Text(b, "label"), key));
        }
        return new(Text(form, "title", "agwinterm question"), Text(form, "prompt", ""), fields.AsReadOnly(), buttons.AsReadOnly());
    }
}

public sealed record AskValue(string Text, IReadOnlyList<string> Selected, string? Other)
{
    public static AskValue Empty(AskField field) => new("", [], field.AllowOther ? "" : null);
}
public sealed record AskOutcome(string Result, string? Reason = null, string? Button = null, IReadOnlyDictionary<string, object>? Values = null)
{
    public Dictionary<string, object> Wire()
    {
        var result = new Dictionary<string, object> { ["result"] = Result };
        if (Reason is not null) result["reason"] = Reason;
        if (Button is not null) result["button"] = Button;
        if (Values is not null) result["values"] = Values;
        return result;
    }
    public static AskOutcome Answer(AskSpec spec, string button, IReadOnlyDictionary<string, AskValue> values)
    {
        if (!spec.Buttons.Any(b => b.Id == button)) throw new ArgumentException("unknown ask button");
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var f in spec.Fields)
        {
            var value = values.GetValueOrDefault(f.Id) ?? AskValue.Empty(f);
            if (f.Type is "text" or "multiline")
            {
                int max = f.Type == "multiline" ? AskSpec.MaxMultiline : AskSpec.MaxText;
                if (value.Text.Length > max || value.Text.Any(c => c == 127 || (c < 32 && !(f.Type == "multiline" && c is '\r' or '\n' or '\t'))))
                    throw new ArgumentException("invalid ask text value");
                result[f.Id] = value.Text;
            }
            else
            {
                var selected = value.Selected.ToHashSet(StringComparer.Ordinal);
                if (selected.Count != value.Selected.Count || (f.Type == "single" && selected.Count > 1) ||
                    selected.Any(id => !f.Choices.Any(c => c.Id == id)) || (!f.AllowOther && value.Other is not null) ||
                    (value.Other is not null && !PickSpec.DisplayText(value.Other))) throw new ArgumentException("invalid ask choice value");
                result[f.Id] = new { selected = f.Choices.Where(c => selected.Contains(c.Id)).Select(c => c.Id).ToArray(), other = value.Other };
            }
        }
        return new("answered", Button: button, Values: result);
    }
}

public sealed record PendingAsk(string Id, string Window, AskSpec Spec);
public sealed record LocatedAsk(string Window, AskOutcome Outcome);

public sealed class AskRegistry
{
    private sealed record Answer(string Id, string Window, AskOutcome Outcome, long Sequence);
    private sealed class WindowState { public PendingAsk? Pending; public List<Answer> Answers = new(); }
    private readonly object _gate = new();
    private readonly Dictionary<string, WindowState> _windows = new(StringComparer.Ordinal);
    private readonly List<Answer> _closed = new();
    private long _sequence;
    public PendingAsk? Open(string window, AskSpec spec)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(window, out var state)) _windows[window] = state = new();
            if (state.Pending is not null) return null;
            return state.Pending = new(Guid.NewGuid().ToString(), window, spec);
        }
    }
    public LocatedAsk? Find(string id)
    {
        lock (_gate)
        {
            foreach (var state in _windows.Values)
            {
                if (state.Pending is { } p && p.Id == id) return new(p.Window, new("pending"));
                if (state.Answers.FirstOrDefault(a => a.Id == id) is { } a) return new(a.Window, a.Outcome);
            }
            return _closed.FirstOrDefault(a => a.Id == id) is { } old ? new(old.Window, old.Outcome) : null;
        }
    }
    public bool Resolve(string id, AskOutcome outcome)
    {
        if (outcome.Result == "cancelled" ? outcome.Reason is not ("cancel" or "escape" or "closed" or "owner-closed") :
            outcome.Result != "answered" || outcome.Button is null || outcome.Values is null) throw new ArgumentException("terminal ask outcome required");
        lock (_gate)
        {
            var state = _windows.Values.FirstOrDefault(s => s.Pending?.Id == id);
            if (state?.Pending is not { } p) return false;
            state.Answers.Add(new(id, p.Window, outcome, ++_sequence)); state.Pending = null;
            if (state.Answers.Count > 8) state.Answers.RemoveAt(0);
            return true;
        }
    }
    public void Abort(string id)
    {
        lock (_gate) foreach (var state in _windows.Values)
            if (state.Pending?.Id == id) { state.Pending = null; return; }
    }
    public void CloseWindow(string window)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(window, out var state)) return;
            if (state.Pending is { } p) Resolve(p.Id, new("cancelled", "owner-closed"));
            _closed.AddRange(state.Answers); _windows.Remove(window);
            _closed.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
            if (_closed.Count > 32) _closed.RemoveRange(0, _closed.Count - 32);
        }
    }
}

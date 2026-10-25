using System.Text;
using System.Text.Json;
using Agwinterm.Core;

namespace Agwinterm.Ctl;

public static class AskCli
{
    private sealed record Options(string Verb, string? Id, Dictionary<string, string> Values, HashSet<string> Flags);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static Options Parse(string[] argv)
    {
        var values = new Dictionary<string, string>(); var flags = new HashSet<string>(); var pos = new List<string>();
        for (int i = 1; i < argv.Length; i++)
        {
            string word = argv[i];
            if (!word.StartsWith("--", StringComparison.Ordinal)) { pos.Add(word); continue; }
            string key = word[2..];
            if (key is "follow" or "no-block" or "json")
            { if (!flags.Add(key)) throw new ArgumentException("duplicate ask option: " + word); continue; }
            if (key is not ("window" or "pipe" or "socket")) throw new ArgumentException("unknown ask option: " + word);
            if (++i >= argv.Length || argv[i].StartsWith("--", StringComparison.Ordinal) || argv[i].Length == 0 || !values.TryAdd(key, argv[i]))
                throw new ArgumentException("missing or duplicate ask option: " + word);
        }
        string verb = pos.FirstOrDefault() ?? "open";
        if (verb is not ("open" or "result" or "cancel") || (verb == "open" ? pos.Count > 1 : pos.Count != 2 || pos[1].Length == 0))
            throw new ArgumentException("ask open reads a JSON form from stdin; result/cancel require one exact ID");
        if (verb != "open" && flags.Any(f => f != "json")) throw new ArgumentException("open-only ask option");
        if (values.ContainsKey("pipe") && values.ContainsKey("socket")) throw new ArgumentException("choose --pipe or --socket");
        return new(verb, verb == "open" ? null : pos[1], values, flags);
    }
    private static string Request(string verb, string? id = null, string? window = null, object? args = null)
    {
        var d = new Dictionary<string, object> { ["cmd"] = "ask." + verb };
        if (id is not null) d["target"] = id;
        if (window is not null) d["window"] = window;
        if (args is not null) d["args"] = args;
        return JsonSerializer.Serialize(d);
    }
    private static JsonElement Success(JsonElement reply)
    {
        if (reply.ValueKind != JsonValueKind.Object || !reply.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException(reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : "invalid ask reply");
        if (!reply.TryGetProperty("result", out var result)) throw new InvalidOperationException("ask reply missing result");
        return result;
    }
    private static JsonElement Outcome(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("ask", out var a) || a.ValueKind != JsonValueKind.Object ||
            !a.TryGetProperty("result", out var state) || state.ValueKind != JsonValueKind.String) throw new InvalidOperationException("invalid ask outcome");
        bool valid = state.GetString() switch
        {
            "pending" => true,
            "cancelled" => a.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String && r.GetString() is "cancel" or "escape" or "closed" or "owner-closed",
            "answered" => a.TryGetProperty("button", out var b) && b.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(b.GetString()) &&
                          a.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Object,
            _ => false
        };
        if (!valid) throw new InvalidOperationException("invalid ask outcome");
        return a;
    }
    private static int Code(JsonElement a) => a.GetProperty("result").GetString() switch { "answered" => 0, "cancelled" => 2, _ => 1 };
    public static int Execute(string[] argv, byte[] input, Func<string, JsonElement> send, Action<int> wait,
        Action<string> output, Action<string> error, CancellationToken cancellation = default)
    {
        Options options; JsonElement form = default;
        try
        {
            options = Parse(argv);
            if (options.Verb == "open")
            {
                if (input.Length > AskSpec.MaxBytes) throw new ArgumentException("ask stdin exceeds 1 MiB");
                using var d = JsonDocument.Parse(Utf8.GetString(input)); AskSpec.Parse(d.RootElement); form = d.RootElement.Clone();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException) { error(ex.Message); return 2; }
        string? pending = null; bool finished = false;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            string? window = options.Values.GetValueOrDefault("window");
            if (options.Verb != "open")
            {
                var result = Success(send(Request(options.Verb, options.Id, window)));
                if (options.Verb == "cancel") { output(JsonSerializer.Serialize(new { id = options.Id, cancelled = true })); return 0; }
                var outcome = Outcome(result); output(outcome.GetRawText()); return Code(outcome);
            }
            var opened = Success(send(Request("open", window: window, args: new { form, follow = options.Flags.Contains("follow") })));
            if (opened.ValueKind != JsonValueKind.Object || !opened.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString()))
                throw new InvalidOperationException("ask.open reply missing id; creation may have occurred");
            pending = id.GetString();
            if (options.Flags.Contains("no-block")) { output(JsonSerializer.Serialize(new { id = pending })); finished = true; return 0; }
            int polls = 0;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var a = Outcome(Success(send(Request("result", pending))));
                if (a.GetProperty("result").GetString() != "pending") { finished = true; output(a.GetRawText()); return Code(a); }
                wait(++polls <= 10 ? 100 : 500);
            }
        }
        catch (Exception ex) { error(ex is OperationCanceledException ? "ask wait cancelled" : ex.Message); return ex is OperationCanceledException ? 130 : 1; }
        finally { if (pending is not null && !finished) try { send(Request("cancel", pending)); } catch { } }
    }
    public static int Run(string[] argv)
    {
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var o = Parse(argv); byte[] input = [];
            if (o.Verb == "open")
            {
                using var data = new MemoryStream(); using var stdin = Console.OpenStandardInput(); var chunk = new byte[8192]; int count;
                while ((count = stdin.ReadAsync(chunk, stop.Token).AsTask().WaitAsync(stop.Token).GetAwaiter().GetResult()) != 0)
                { if (data.Length + count > AskSpec.MaxBytes) throw new ArgumentException("ask stdin exceeds 1 MiB"); data.Write(chunk, 0, count); }
                input = data.ToArray();
            }
            string pipe = o.Values.GetValueOrDefault("pipe") ?? o.Values.GetValueOrDefault("socket") ?? Environment.GetEnvironmentVariable("AGWINTERM_PIPE") ?? "agwinterm";
            return Execute(argv, input, r => PickCli.Send(pipe, r), ms => { if (stop.Token.WaitHandle.WaitOne(ms)) stop.Token.ThrowIfCancellationRequested(); }, Console.WriteLine, Console.Error.WriteLine, stop.Token);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return ex is OperationCanceledException ? 130 : ex is ArgumentException ? 2 : 1; }
        finally { Console.CancelKeyPress -= handler; }
    }
}

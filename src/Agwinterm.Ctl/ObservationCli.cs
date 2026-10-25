using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Agwinterm.Ctl;

/// <summary>Observation-specific outcomes; a timeout is never mistaken for a matched status.</summary>
public static class ObservationCli
{
    public static int Run(string pipeName, Dictionary<string, object?> request, Dictionary<string, object?> args,
        Dictionary<string, string> options, bool json)
    {
        using var cancelled = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancelled.Cancel(); };
        Console.CancelKeyPress += handler;
        try { return RunAsync(pipeName, request, args, options, json, Console.Out, Console.Error, cancelled.Token).GetAwaiter().GetResult(); }
        finally { Console.CancelKeyPress -= handler; }
    }

    public static async Task<int> RunAsync(string pipeName, Dictionary<string, object?> request,
        Dictionary<string, object?> args, IReadOnlyDictionary<string, string> options, bool json,
        TextWriter output, TextWriter errors, CancellationToken cancellationToken)
    {
        bool watch = Equals(request["cmd"], "events");
        if (!CtlRequest.TryResolveTimeout(options.GetValueOrDefault("timeout"), false, out var timeout, out var timeoutError))
        { await errors.WriteLineAsync(timeoutError); return 2; }
        if (watch)
        {
            if (args.TryGetValue("since", out var value) && Convert.ToInt64(value) > 0 &&
                (!options.ContainsKey("run") || !options.TryGetValue("window", out var window) || window == "active"))
            { await errors.WriteLineAsync("watch resume after zero requires --run and an exact --window. Nothing sent."); return 2; }
            if (!args.ContainsKey("since")) args["tail"] = true;
            args["wait-ms"] = 0;
        }
        else if (!options.ContainsKey("timeout"))
        {
            int wait = Convert.ToInt32(args["timeout-ms"]);
            timeout = wait == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(wait + 5000);
        }
        request["args"] = args;
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(CtlRequest.ConnectTimeoutMs, cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
            string? pinnedRun = options.GetValueOrDefault("run");
            string? pinnedWindow = args.ContainsKey("since") && Convert.ToInt64(args["since"]) > 0 ? options.GetValueOrDefault("window") : null;
            long previousCursor = args.TryGetValue("since", out var start) ? Convert.ToInt64(start) : 0;
            while (true)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (timeout != Timeout.InfiniteTimeSpan) deadline.CancelAfter(timeout);
                string? response;
                try
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request) + "\n");
                    await pipe.WriteAsync(bytes, CancellationToken.None).AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
                    response = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { throw new IOException("observation transport deadline expired; no server-confirmed outcome"); }
                if (response is null) throw new IOException("observation connection closed without a reply");
                using var document = JsonDocument.Parse(response);
                var root = document.RootElement;
                if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("invalid observation reply envelope");
                if (!ok.GetBoolean()) return CtlReply.Print(response, json, false, output, errors);
                if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("missing observation result");
                if (!watch)
                {
                    string outcome = RequiredString(result, "outcome");
                    RequiredString(result, "run"); RequiredString(result, "window");
                    string scope = RequiredString(result, "scope");
                    if (scope != (string)args["scope"]! || Integer(result, "elapsedMs") < 0) throw new InvalidDataException("invalid wait identity");
                    RequiredString(result, "paneId");
                    if (scope == "session") RequiredString(result, "sessionId");
                    if (outcome == "matched")
                    {
                        string status = RequiredString(result, "status"), source = RequiredString(result, "source");
                        if (!((string[])args["statuses"]!).Contains(status) || source is not ("snapshot" or "transition") || Integer(result, "seq") < 0)
                            throw new InvalidDataException("invalid wait match");
                        await output.WriteLineAsync(json ? response : status); await output.FlushAsync(); return 0;
                    }
                    if (outcome != "timeout" || result.TryGetProperty("status", out _) || result.TryGetProperty("seq", out _) || result.TryGetProperty("source", out _))
                        throw new InvalidDataException("invalid wait outcome");
                    if (json) { await output.WriteLineAsync(response); await output.FlushAsync(); }
                    else await errors.WriteLineAsync("status wait timed out");
                    return 3;
                }
                if (Integer(result, "schema") != 1) throw new InvalidDataException("unsupported event schema");
                string run = RequiredString(result, "run"), owner = RequiredString(result, "window");
                if ((pinnedRun is not null && pinnedRun != run) || (pinnedWindow is not null && pinnedWindow != owner))
                    throw new InvalidDataException("event observation identity changed");
                long oldest = Integer(result, "oldest"), head = Integer(result, "head"), cursor = Integer(result, "cursor");
                bool gap = result.GetProperty("gap").GetBoolean(), more = result.GetProperty("more").GetBoolean();
                var events = result.GetProperty("events");
                if (oldest < 1 || head < 0 || cursor < previousCursor || cursor > head || oldest > head + 1 || more != (cursor < head) || events.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("invalid event page bounds");
                long last = previousCursor;
                foreach (var item in events.EnumerateArray())
                {
                    long seq = Integer(item, "seq");
                    if (seq <= last || seq > cursor || seq < oldest || RequiredString(item, "window") != owner)
                        throw new InvalidDataException("invalid event sequence or owner");
                    RequiredString(item, "type"); last = seq;
                }
                if (json) await output.WriteLineAsync(response);
                else if (!gap)
                {
                    foreach (var item in events.EnumerateArray())
                    {
                        string Field(string key) => item.TryGetProperty(key, out var field) ? field.GetString() ?? "-" : "-";
                        string info = item.TryGetProperty("info", out var text) ? text.GetRawText() : "null";
                        await output.WriteLineAsync($"{Integer(item, "seq")}\t{Field("type")}\t{owner}\t{Field("sessionId")}\t{Field("paneId")}\t{info}");
                    }
                    await errors.WriteLineAsync($"run={run} window={owner} cursor={cursor} head={head}");
                }
                await output.FlushAsync();
                if (gap) { await errors.WriteLineAsync($"event history gap: oldest={oldest}, cursor={cursor}"); return 1; }
                pinnedRun = run; pinnedWindow = owner; previousCursor = cursor;
                request["window"] = owner; args["run"] = run; args["since"] = cursor; args.Remove("tail");
                args["wait-ms"] = more ? 0 : 25000;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await errors.WriteLineAsync("observation cancelled");
            if (!watch && json) { await output.WriteLineAsync("{\"ok\":false,\"code\":\"cancelled\",\"source\":\"client\",\"error\":\"cancelled\"}"); await output.FlushAsync(); }
            return 130;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException or JsonException or InvalidOperationException or KeyNotFoundException)
        { await errors.WriteLineAsync("observation failed: " + ex.Message); return 1; }
    }

    private static string RequiredString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString())
            ? value.GetString()! : throw new InvalidDataException("missing or invalid " + name);
    private static long Integer(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)
            ? number : throw new InvalidDataException("missing or invalid " + name);
}

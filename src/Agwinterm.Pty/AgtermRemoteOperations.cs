using System.Text;
using System.Text.Json;

namespace Agwinterm.Pty;

public sealed class AgtermRemoteOperations
{
    public async Task<AgtermRemoteBinding> ProbeAsync(AgtermSshTarget target, string bridge, AgtermEndpoint endpoint,
        AgtermSession session, AgtermPane pane, CancellationToken ct = default, int? presentationVersion = null)
    {
        var response = await RequestAsync(target, bridge, new { verb = "probe", executable = endpoint.Executable,
            socketDirectory = endpoint.SocketDirectory, name = pane.Daemon }, ct).ConfigureAwait(false);
        if (response.GetProperty("protocol").GetInt32() != 1 || !response.GetProperty("existingOnly").GetBoolean())
            throw new InvalidOperationException("Origin lacks existing-only attachment");
        var binding = new AgtermRemoteBinding(1, "agterm-ssh", Guid.NewGuid().ToString(), target, bridge, endpoint,
            session.Id, session.WindowId, session.WorkspaceId, pane.Role, pane.Daemon,
            response.GetProperty("identity").Clone(), response.GetProperty("clientSha256").GetString()!,
            PresentationVersion: presentationVersion is > 0 ? presentationVersion : null);
        if (!binding.IsValid) throw new InvalidOperationException("Origin returned an invalid identity");
        return binding;
    }

    public async Task RevalidateAsync(AgtermRemoteBinding binding, CancellationToken ct = default)
    {
        var result = await RequestAsync(binding.Target, binding.Bridge, new { verb = "probe", executable = binding.Endpoint.Executable,
            socketDirectory = binding.Endpoint.SocketDirectory, name = binding.Daemon }, ct).ConfigureAwait(false);
        if (!JsonElement.DeepEquals(result.GetProperty("identity"), binding.Identity)
            || result.GetProperty("clientSha256").GetString() != binding.ClientSha256)
            throw new InvalidOperationException("Origin workload or client build changed; refusing replacement");
    }

    public async Task<JsonElement> ReadQuestionResultAsync(AgtermRemoteBinding binding, string id, CancellationToken ct)
    {
        if (id.Length is < 1 or > 256 || id.Any(char.IsControl)) throw new ArgumentException("Invalid question ID");
        await RevalidateAsync(binding, ct).ConfigureAwait(false);
        string command = "PATH=\"$PATH:$HOME/.local/bin:/usr/local/bin:/opt/homebrew/bin\"; export PATH; exec agtermctl ask result "
            + AgtermSshDiscoveryClient.QuoteAccountShell(id) + " --json"
            + (binding.Target.OriginSocket is null ? "" : " --socket " + AgtermSshDiscoveryClient.QuoteAccountShell(binding.Target.OriginSocket));
        var reply = await AgtermSshDiscoveryClient.RunAsync(AgtermSshDiscoveryClient.CommandInfo(binding.Target, false,
            string.Join(' ', new[] { "/bin/sh", "-c", command }.Select(AgtermSshDiscoveryClient.QuoteAccountShell))), TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(reply.Stdout);
        AgtermRemoteDiscovery.RejectDuplicates(doc.RootElement);
        var value = doc.RootElement;
        if (value.TryGetProperty("ok", out var ok))
        {
            if (ok.ValueKind != JsonValueKind.True) throw new IOException("Origin result unavailable");
            value = value.GetProperty("result");
            if (value.TryGetProperty("ask", out var ask)) value = ask;
        }
        if (value.GetProperty("result").GetString() is not ("pending" or "answered" or "escaped" or "cancelled"))
            throw new IOException("Invalid origin question outcome");
        return value.Clone();
    }

    private static object Request(AgtermRemoteBinding binding, string verb, string? text = null, bool all = false,
        string? nonce = null, bool claim = false) => new { verb, executable = binding.Endpoint.Executable,
            socketDirectory = binding.Endpoint.SocketDirectory, name = binding.Daemon, identity = binding.Identity,
            clientSha256 = binding.ClientSha256, text, all, nonce, claim };

    public Task<JsonElement> ScreenAsync(AgtermRemoteBinding binding, bool all, CancellationToken ct = default) =>
        RequestAsync(binding.Target, binding.Bridge, Request(binding, "screen", all: all), ct);

    public async Task<JsonElement> TypeAsync(AgtermRemoteBinding binding, string text, CancellationToken ct = default)
    {
        if (Encoding.UTF8.GetByteCount(text) is 0 or > 32768) throw new ArgumentException("Input exceeds limit");
        try
        {
            var result = await RequestAsync(binding.Target, binding.Bridge, Request(binding, "type", text), ct).ConfigureAwait(false);
            RequireInputAcknowledgement(result);
            return result;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException or JsonException or KeyNotFoundException)
        { throw new WslRecoveryException("uncertain", "Remote input delivery is uncertain; it was not retried"); }
    }

    internal static void RequireInputAcknowledgement(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("accepted", out var accepted) || accepted.ValueKind != JsonValueKind.True)
            throw new IOException("Remote input was not acknowledged");
    }

    public static string[] AttachmentArgs(AgtermRemoteBinding binding, string nonce, bool claim = false)
    {
        if (!binding.IsValid) throw new ArgumentException("Invalid remote binding");
        string encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(Request(binding, "attach", nonce: nonce, claim: claim)));
        if (encoded.Length > 16000) throw new ArgumentException("Attachment request exceeds limit");
        string command = string.Join(' ', new[] { binding.Bridge, "--request", encoded }.Select(AgtermSshDiscoveryClient.QuoteAccountShell));
        return AgtermSshDiscoveryClient.CommandInfo(binding.Target, true, command).ArgumentList.ToArray();
    }

    private static async Task<JsonElement> RequestAsync(AgtermSshTarget target, string bridge, object request, CancellationToken ct)
    {
        if (!AgtermRemoteDiscovery.OriginPath(bridge)) throw new ArgumentException("Invalid bridge path");
        string input = JsonSerializer.Serialize(request);
        if (Encoding.UTF8.GetByteCount(input) > 65536) throw new ArgumentException("Bridge request exceeds limit");
        var response = await AgtermSshDiscoveryClient.RunAsync(AgtermSshDiscoveryClient.CommandInfo(target, false,
            AgtermSshDiscoveryClient.QuoteAccountShell(bridge)), TimeSpan.FromSeconds(20), ct, input).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(response.Stdout);
        AgtermRemoteDiscovery.RejectDuplicates(doc.RootElement);
        if (response.ExitCode != 0)
        {
            if (doc.RootElement.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String && state.GetString() == "unsupported")
                throw new IOException("Origin requires the verified ZMX client/daemon pair with primary-history replay support");
            throw new IOException("Origin bridge failed or is unavailable");
        }
        if (!doc.RootElement.GetProperty("ok").GetBoolean()) throw new IOException("Origin bridge refused operation");
        return doc.RootElement.GetProperty("result").Clone();
    }
}

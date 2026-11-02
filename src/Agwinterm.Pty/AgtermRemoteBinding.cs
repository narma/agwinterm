using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agwinterm.Pty;

public sealed record AgtermRemoteBinding(int Version, string Provider, string BindingId,
    AgtermSshTarget Target, string Bridge, AgtermEndpoint Endpoint, string OriginSession,
    string OriginWindow, string OriginWorkspace, string Role, string Daemon, JsonElement Identity,
    string ClientSha256, string Recovery = "pending", string? TransportTicket = null,
    string? AttachmentNonce = null, string? Error = null)
{
    [JsonIgnore] public string Ownership => "borrowed";
    [JsonIgnore] public bool IsValid
    {
        get
        {
            try { Target.Validate(); } catch { return false; }
            return Version == 1 && Provider == "agterm-ssh" && Guid.TryParseExact(BindingId, "D", out _)
                && AgtermRemoteDiscovery.OriginPath(Bridge) && Endpoint is not null
                && AgtermRemoteDiscovery.OriginPath(Endpoint.Executable) && AgtermRemoteDiscovery.OriginPath(Endpoint.SocketDirectory)
                && new[] { OriginSession, OriginWindow, OriginWorkspace }.All(s => WslLiveValidation.Text(s))
                && Role is "left" or "right" && Daemon is not null && AgtermRemoteDiscovery.DaemonName(Daemon)
                && ClientSha256 is { Length: 64 } && ClientSha256.All(char.IsAsciiHexDigitLower)
                && Recovery is "pending" or "host-adopted" or "remote-reattached" or "unavailable" or "uncertain" or "missing"
                && ValidIdentity(Identity);
        }
    }

    private bool ValidIdentity(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        try
        {
            string? platform = value.GetProperty("platform").GetString();
            if (platform is not ("macos" or "linux")) return false;
            foreach (var name in new[] { "daemon", "workload" })
            {
                var process = value.GetProperty(name);
                if (process.GetProperty("pid").GetInt32() <= 1 || !Guid.TryParse(process.GetProperty("boot").GetString(), out _)) return false;
                if (platform == "macos")
                {
                    if (process.GetProperty("startSeconds").GetUInt64() == 0 || process.GetProperty("startMicros").GetUInt64() >= 1000000) return false;
                    _ = process.GetProperty("uid").GetUInt32();
                }
                else if (!ulong.TryParse(process.GetProperty("start").GetString(), out var start) || start == 0
                    || !WslLiveValidation.Text(process.GetProperty("namespace").GetString())) return false;
            }
            var socket = value.GetProperty("socket");
            return socket.GetArrayLength() == 2 && socket[0].TryGetUInt64(out _) && socket[1].GetUInt64() > 0
                && value.GetProperty("path").GetString() == Endpoint.SocketDirectory.TrimEnd('/') + "/" + Daemon
                && AgtermRemoteDiscovery.OriginPath(value.GetProperty("executable").GetString()!);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException) { return false; }
    }

    public static bool IsRemote(JsonElement raw) => raw.ValueKind == JsonValueKind.Object
        && raw.TryGetProperty("Provider", out var provider) && provider.ValueKind == JsonValueKind.String && provider.GetString() == "agterm-ssh";

    public static AgtermRemoteBinding? Load(JsonElement raw, out string? error)
    {
        error = null;
        try
        {
            AgtermRemoteDiscovery.RejectDuplicates(raw);
            var binding = raw.Deserialize<AgtermRemoteBinding>();
            if (binding is { IsValid: true }) return binding;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or IOException or InvalidOperationException) { }
        error = "Invalid or unsupported remote binding; no commands were replayed";
        return null;
    }
}

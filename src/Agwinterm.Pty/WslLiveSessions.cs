using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agwinterm.Pty;

[JsonConverter(typeof(WslZmxConfigurationConverter))]
public sealed record WslZmxConfiguration(bool Enabled = false, string? Executable = null, string? Error = null);

/// <summary>Malformed opt-in stays an explicit refused request, never a plain-shell fallback.</summary>
public sealed class WslZmxConfigurationConverter : JsonConverter<WslZmxConfiguration>
{
    public override WslZmxConfiguration Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return new(true, Error: "zmx must be an object");
        bool enabled = false;
        string? executable = null;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
        {
            if (!keys.Add(field.Name)) return new(true, Error: "Duplicate zmx option");
            switch (field.Name)
            {
                case "enabled" when field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    enabled = field.Value.GetBoolean(); break;
                case "executable" when field.Value.ValueKind == JsonValueKind.String:
                    executable = field.Value.GetString(); break;
                default: return new(true, Error: "Unknown or invalid zmx option: " + field.Name);
            }
        }
        return new(enabled, executable);
    }
    public override void Write(Utf8JsonWriter writer, WslZmxConfiguration value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("enabled", value.Enabled);
        if (value.Executable is not null) writer.WriteString("executable", value.Executable);
        // Preserve refusal across a settings save instead of normalizing an invalid request to valid.
        if (value.Error is not null) writer.WriteString("invalid", value.Error);
        writer.WriteEndObject();
    }
}

public enum WslLiveSurface { Ordinary, Quick, Scratch, Program, Handoff }

public static class WslLiveProfiles
{
    public static string? Validate(ShellProfile profile, string host, WslLiveSurface surface)
    {
        if (profile.Zmx is not { } config) return null;
        if (config.Error is not null) return config.Error;
        if (!config.Enabled) return null;
        if (!WslIntegration.IsWsl(profile.Command)) return "ZMX requires a WSL profile";
        if (surface != WslLiveSurface.Ordinary) return "ZMX requires an ordinary session pane";
        if (host != "server-rust") return "ZMX requires the Rust PTY host";
        if (!WslLiveValidation.LinuxPath(config.Executable)) return "ZMX executable must be an absolute Linux path";
        return null;
    }

    /// <summary>Resolve defaults before persistence; helpers always use these exact selectors.</summary>
    public static string[] ResolvedSelectors(string distribution, string user)
    {
        if (!WslLiveValidation.Text(distribution) || !WslLiveValidation.Text(user))
            throw new ArgumentException("Resolved distribution and Linux user are required");
        return ["--distribution", distribution, "--user", user];
    }

    public static string[] WorkloadArgv(ShellProfile profile)
    {
        var args = profile.Args ?? [];
        int i = 0;
        while (i < args.Length)
        {
            string arg = args[i++];
            if (arg is "--exec" or "-e" or "--")
                return i < args.Length ? args[i..] : throw new ArgumentException("WSL command is empty");
            if (!arg.StartsWith('-')) return args[(i - 1)..];
            if (arg is "-d" or "--distribution" or "-u" or "--user" or "--cd" or "--shell-type")
            {
                if (i >= args.Length) throw new ArgumentException("WSL option requires a value");
                i++;
            }
            else if (!(arg.StartsWith("--distribution=", StringComparison.Ordinal) ||
                       arg.StartsWith("--user=", StringComparison.Ordinal) || arg.StartsWith("--cd=", StringComparison.Ordinal)))
                throw new ArgumentException("Unsupported live WSL launch option: " + arg);
        }
        return ["/bin/sh", "-c", "exec \"${SHELL:-/bin/sh}\" -l"];
    }

    public static Dictionary<string, string> WorkloadEnvironment(IReadOnlyDictionary<string, string>? environment)
        => (environment ?? new Dictionary<string, string>())
            .Where(kv => !kv.Key.StartsWith("ZMX_", StringComparison.Ordinal) && kv.Key != "WSLENV")
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
}

public static class WslLiveValidation
{
    public static bool Text(string? value, int max = 256) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= max && !value.Any(char.IsControl);
    public static bool LinuxPath(string? value) => Text(value, 4096) && value![0] == '/'
        && !value.Contains('\\') && !value.Split('/').Contains("..");
}

public sealed record WslLiveEndpoint(string Id, string Distribution, string User, string Executable,
    string SocketDirectory, string OriginId, string OriginKind = "local-wsl",
    string? LedgerDirectory = null, string? HelperPath = null)
{
    [JsonIgnore] public bool IsValid => Guid.TryParseExact(Id, "D", out _) && WslLiveValidation.Text(Distribution)
        && WslLiveValidation.Text(User) && WslLiveValidation.LinuxPath(Executable)
        && WslLiveValidation.LinuxPath(SocketDirectory) && !SocketDirectory.StartsWith("/mnt/", StringComparison.Ordinal)
        && Encoding.UTF8.GetByteCount(SocketDirectory) + 40 < 108
        && WslLiveValidation.Text(OriginId) && OriginKind == "local-wsl"
        && (LedgerDirectory is null || WslLiveValidation.LinuxPath(LedgerDirectory))
        && (HelperPath is null || WslLiveValidation.LinuxPath(HelperPath));
    [JsonIgnore] public string[] Selectors => WslLiveProfiles.ResolvedSelectors(Distribution, User);
}

public sealed record LinuxProcessIncarnation(int Pid, string BootId, ulong StartTicks, string PidNamespace)
{
    [JsonIgnore] public bool IsValid => Pid > 1 && Guid.TryParseExact(BootId, "D", out _) && StartTicks > 0
        && PidNamespace is not null && PidNamespace.StartsWith("pid:[", StringComparison.Ordinal) && PidNamespace.EndsWith(']')
        && ulong.TryParse(PidNamespace[5..^1], out _);
}

public sealed record WslDaemonIdentity(string Name, LinuxProcessIncarnation Daemon,
    LinuxProcessIncarnation Workload, ulong SocketDevice, ulong SocketInode)
{
    [JsonIgnore] public bool IsValid => Name is { Length: 39 } && Name.StartsWith("agterm-", StringComparison.Ordinal)
        && Name[7..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
        && Daemon is { IsValid: true } && Workload is { IsValid: true } && SocketInode > 0;
}

public sealed record WslRestartOrigin(string BindingId, WslDaemonIdentity Identity)
{
    [JsonIgnore] public bool IsValid => Guid.TryParseExact(BindingId, "D", out _) && Identity is { IsValid: true };
}

public sealed record WslLiveBinding(int Version, string Provider, string BindingId, string OriginPaneId,
    WslLiveEndpoint Endpoint, WslDaemonIdentity? Identity, string Ownership, string Recovery,
    string? TransportTicket = null, string? Error = null, bool Pending = false, string? AttachmentNonce = null,
    WslRestartOrigin? RestartOrigin = null)
{
    public static readonly string[] Outcomes = ["host-adopted", "zmx-reattached", "fresh-started", "missing", "unavailable", "unsupported", "uncertain"];
    [JsonIgnore] public bool IsValid => Version == 1 && Provider == "wsl-zmx" && Guid.TryParseExact(BindingId, "D", out _)
        && WslLiveValidation.Text(OriginPaneId) && Endpoint is { IsValid: true }
        && (Identity is { IsValid: true } || (Pending && Identity is null && Recovery is "uncertain" or "unsupported" or "unavailable"
            && Ownership == "owned" && TransportTicket is null && Endpoint.LedgerDirectory is not null && Endpoint.HelperPath is not null))
        && Ownership is "owned" or "external" && Outcomes.Contains(Recovery)
        && (RestartOrigin is null || (RestartOrigin.IsValid && RestartOrigin.BindingId != BindingId
            && Pending && Identity is null && Ownership == "owned"));
    [JsonIgnore] public bool SuppressesReplay => true;
}

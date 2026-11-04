using System.Text;
using System.Text.Json;

namespace Agwinterm.Pty;

/// <summary>Remote sound names never authorize reading a viewer-local file or arbitrary sound alias.</summary>
public static class AgtermPresentationSound
{
    public static string? Read(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) throw new IOException("Invalid sound payload");
        if (!body.TryGetProperty("name", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new IOException("Invalid sound name");
        var name = value.GetString()!;
        if (Encoding.UTF8.GetByteCount(name) > 4096 || name.Any(char.IsControl)) throw new IOException("Invalid sound name");
        return name;
    }

    // Same policy as the pinned Mac viewer: an unknown origin name falls back to the system alert.
    // Only the local preference (null wire name) may point to a local WAV file.
    public static string? ViewerSound(string? name, string preference) => name is null
        ? (string.IsNullOrWhiteSpace(preference) || preference.Equals("off", StringComparison.OrdinalIgnoreCase)
            || preference.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : preference)
        : name.Length == 0 ? null : name.ToLowerInvariant() switch
        { "default" or "beep" or "asterisk" or "exclamation" or "hand" or "error" or "critical" or "question" => name,
            _ => "default" };

    // Host-specific files and aliases remain at their origin. Portable names have the same contract.
    public static bool Portable(string? name) => name is null || name is "default" or "beep";
}

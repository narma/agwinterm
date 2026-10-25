using System.Text;
using System.Text.Json;

namespace Agwinterm.Pty;

public sealed record ClosedSession(string? CustomName, string? ProfileName, string Cwd,
    string WorkspaceId, string WorkspaceName, bool Flagged, string Display, string Name,
    string SessionId, long Seq, string? Context, SessionState State, string SourceWindowId,
    string EntryId);
public sealed record ClosedWorkspace(string Name, IReadOnlyList<ClosedSession> Sessions, long Seq,
    string WorkspaceId, string SourceWindowId, string EntryId, bool Expanded = true,
    string? DestinationWindowId = null, string? DestinationWorkspaceId = null);

/// <summary>UI-thread-owned history. Its file is independent of live restore checkpoints.</summary>
public sealed class RecentHistory
{
    public const int MaxBytes = 8 * 1024 * 1024;
    public List<ClosedSession> Sessions { get; set; } = [];
    public List<ClosedWorkspace> Workspaces { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public string? Warning { get; set; }
    public long NextSequence() => Math.Max(Sessions.Select(s => s.Seq).DefaultIfEmpty().Max(),
        Workspaces.Select(w => w.Seq).DefaultIfEmpty().Max()) + 1;

    public void Bound()
    {
        Sessions.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        Workspaces.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        while (Sessions.Count > 25) EvictSession();
        while (Workspaces.Count > 25) EvictWorkspace();
        while (Sessions.Count + Workspaces.Sum(w => w.Sessions.Count) > 1024 ||
            Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(this)) > MaxBytes)
        {
            if (Sessions.Count == 0 && Workspaces.Count == 0) break;
            if (Workspaces.Count == 0 || Sessions.Count > 0 && Sessions[0].Seq <= Workspaces[0].Seq) EvictSession();
            else EvictWorkspace();
        }
    }
    private void EvictSession() { Sessions.RemoveAt(0); Warning = "Oldest recent entries evicted to keep history bounded"; }
    private void EvictWorkspace() { Workspaces.RemoveAt(0); Warning = "Oldest recent entries evicted to keep history bounded"; }
    public static bool ValidId(string? id) => id is { Length: > 0 and <= 256 } && !id.Any(char.IsWhiteSpace) && !id.Any(char.IsControl);
    private static bool Text(string? value, int max = 4096) => value is null || value.Length <= max && !value.Contains('\0');
    private static bool Valid(ClosedSession s) => s is not null && Guid.TryParse(s.EntryId, out _) &&
        ValidId(s.SessionId) && ValidId(s.WorkspaceId) && ValidId(s.SourceWindowId) && s.Seq >= 0 && s.Seq < long.MaxValue &&
        Text(s.CustomName) && Text(s.ProfileName) && Text(s.Cwd) && Text(s.WorkspaceName) && Text(s.Display) && Text(s.Name) &&
        Text(s.Context) && s.State is { } state && state.Id == s.SessionId && Text(state.Name) && Text(state.CustomName) &&
        Text(state.Profile) && Text(state.Context) && state.Panes is { Count: >= 1 and <= 2 } &&
        state.Panes.All(p => p is not null && ValidId(p.Id) && Text(p.Cwd) && Text(p.RestoreCommand, 16384) &&
            float.IsFinite(p.FontSize) && p.FontSize >= 0 && p.FontSize <= 256 && float.IsFinite(p.Ratio) && p.Ratio > 0 && p.Ratio <= 1000) &&
        state.Panes.Select(p => p.Id).Distinct().Count() == state.Panes.Count;

    public static RecentHistory Load(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxBytes) throw new FormatException("history exceeds 8 MiB");
            var h = JsonSerializer.Deserialize<RecentHistory>(stream) ?? throw new FormatException("empty history");
            if (h.Sessions is null || h.Workspaces is null || h.Sessions.Count > 25 || h.Workspaces.Count > 25 ||
                h.Sessions.Any(s => !Valid(s)) || h.Workspaces.Any(w => w is null || !Guid.TryParse(w.EntryId, out _) ||
                    !ValidId(w.WorkspaceId) || !ValidId(w.SourceWindowId) || !Text(w.Name) || w.Seq < 0 || w.Seq == long.MaxValue ||
                    w.Sessions is null || w.Sessions.Count > 1024 || w.Sessions.Any(s => !Valid(s)) ||
                    (w.DestinationWindowId is null) != (w.DestinationWorkspaceId is null) ||
                    w.DestinationWindowId is not null && (!ValidId(w.DestinationWindowId) || !ValidId(w.DestinationWorkspaceId))))
                throw new FormatException("invalid history values");
            var ids = h.Sessions.Select(s => s.EntryId).Concat(h.Workspaces.Select(w => w.EntryId)).ToList();
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count) throw new FormatException("duplicate entry IDs");
            h.Bound(); return h;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            try { File.Move(path, path + ".bad", true); } catch { }
            return new() { Warning = "Recent history unavailable: " + e.Message };
        }
    }

    public bool Save(string path)
    {
        string temporary = path + ".tmp";
        try
        {
            Bound();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(this), new UTF8Encoding(false));
            File.Move(temporary, path, true);
            if (Warning?.StartsWith("Recent history not saved:", StringComparison.Ordinal) == true) Warning = null;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { Warning = "Recent history not saved: " + e.Message; return false; }
        finally { try { File.Delete(temporary); } catch { } }
    }
}

public interface IRecentHost
{
    object RecentList();
    object RecentOpen(string entryId, string? window);
    object RecentClear(string? entryId);
}

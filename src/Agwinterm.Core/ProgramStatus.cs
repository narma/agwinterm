using System.Globalization;
using System.Text;

namespace Agwinterm.Core;

/// <summary>A validated OSC 7501 record. Reports replace records, never patch their fields.</summary>
public sealed record ProgramStatus(string State, string Id, string App, string Title, string Message,
    string? Kind, int? Progress)
{
    public AgentStatus Status => State switch
    {
        "working" => AgentStatus.Active,
        "blocked" or "error" => AgentStatus.Blocked,
        "done" => AgentStatus.Completed,
        _ => AgentStatus.Idle
    };

    internal static ProgramStatus FromNative(byte[] bytes)
    {
        if (bytes.Length < 7 || bytes[0] > 5 || bytes[1] > 3 || bytes[^1] != 0)
            throw new InvalidDataException("Invalid native program status.");
        string[] fields = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 4).Split('\0');
        if (fields.Length != 4) throw new InvalidDataException("Invalid native program status fields.");
        string[] states = ["idle", "working", "done", "blocked", "error", "clear"];
        string?[] kinds = [null, "permission", "question", "auth"];
        return new(states[bytes[0]], fields[0], fields[1], Clean(fields[2]), Clean(fields[3]),
            kinds[bytes[1]], bytes[2] <= 100 ? bytes[2] : null);
    }

    private static string Clean(string value) => string.Concat(value.Where(c =>
        CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format));
}

/// <summary>Bounded per-terminal records; callers serialize access with the terminal lock.</summary>
public sealed class ProgramStatusRecords
{
    private readonly Dictionary<string, ProgramStatus> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _updated = new(StringComparer.Ordinal);
    private long _sequence;
    public IReadOnlyCollection<ProgramStatus> Records => _records.Values;
    public void FinishCommand()
    {
        foreach (string id in _records.Values.Where(r => r.State is "working" or "blocked").Select(r => r.Id).ToArray())
        { _records.Remove(id); _updated.Remove(id); }
    }
    public void Apply(ProgramStatus report)
    {
        if (report.State == "clear")
        {
            foreach (string id in _records.Keys.Where(id => report.Id.Length == 0 || id == report.Id ||
                id.StartsWith(report.Id + "/", StringComparison.Ordinal)).ToArray())
            { _records.Remove(id); _updated.Remove(id); }
            return;
        }
        if (!_records.ContainsKey(report.Id) && _records.Count >= 64)
        {
            string oldest = _updated.MinBy(pair => pair.Value).Key;
            _records.Remove(oldest); _updated.Remove(oldest);
        }
        _records[report.Id] = report;
        _updated[report.Id] = ++_sequence;
    }
}

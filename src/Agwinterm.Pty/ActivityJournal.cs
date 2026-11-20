using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agwinterm.Pty;

public enum ActivityLevel { Info, Warning, Error }

public sealed record ActivityEntry(DateTimeOffset Time, string Source, ActivityLevel Level, string Message, string? Detail)
{
    private string Prefix => $"{Time.ToLocalTime():yyyy-MM-dd HH:mm:ss}  [{Source}] [{Level}]  ";
    public string Heading => Prefix + Message.ReplaceLineEndings(" ");
    public string FullText => Prefix + Message.ReplaceLineEndings(Environment.NewLine)
        + (string.IsNullOrEmpty(Detail) ? "" : Environment.NewLine + Detail.ReplaceLineEndings(Environment.NewLine));
}

/// <summary>One rotating journal per app identity, shared by its windows and host processes.</summary>
public sealed class ActivityJournal(string directory)
{
    private readonly string _path = Path.Combine(directory, "activity.jsonl");
    private readonly string _mutexName = "Agwinterm.Activity." + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToUpperInvariant())));
    private const int MaxBytes = 512 * 1024;

    public (DateTime Modified, long Length) Version
    {
        get
        {
            try { var file = new FileInfo(_path); return file.Exists ? (file.LastWriteTimeUtc, file.Length) : default; }
            catch (IOException) { return default; }
            catch (UnauthorizedAccessException) { return default; }
        }
    }

    public void Write(string source, ActivityLevel level, string message, string? detail = null)
    {
        try
        {
            using var mutex = new Mutex(false, _mutexName);
            try { if (!mutex.WaitOne(TimeSpan.FromSeconds(1))) return; }
            catch (AbandonedMutexException) { } // The previous writer exited. We now own the lock.
            try
            {
                Directory.CreateDirectory(directory);
                if (new FileInfo(_path) is { Exists: true, Length: >= MaxBytes })
                    File.Move(_path, _path + ".1", true);
                var entry = new ActivityEntry(DateTimeOffset.UtcNow, source, level, Clip(message), detail is null ? null : Clip(detail));
                File.AppendAllText(_path, JsonSerializer.Serialize(entry, JsonOptions) + "\n");
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Clip(string text) => text.Length <= 8192 ? text : text[..8191] + "…";
    private static readonly JsonSerializerOptions JsonOptions = new() { IgnoreReadOnlyProperties = true };

    public IReadOnlyList<ActivityEntry> Read()
    {
        var entries = new List<ActivityEntry>();
        foreach (string path in new[] { _path + ".1", _path })
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    try
                    {
                        if (JsonSerializer.Deserialize<ActivityEntry>(line) is { Message: not null, Source: not null } entry)
                            entries.Add(entry);
                    }
                    catch (JsonException) { } // Ignore an interrupted write.
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        entries.Reverse();
        return entries;
    }
}

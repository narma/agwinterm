using System.Globalization;

namespace Agwinterm.Core;

/// <summary>A shell-instance capability and monotonically increasing prompt number. Terminal
/// transcripts cannot clear recovery with a bare marker, a different shell's marker, or an old
/// prompt. The PTY owner checkpoints accepted prompts even while the UI is disconnected.</summary>
public sealed class ShellCompletion
{
    public string Token { get; }
    private readonly string _path;
    private readonly object _gate = new();
    private long _observed;
    public ShellCompletion(string token, string path) { Token = token; _path = path; }
    public static bool ValidToken(string? token) => token is { Length: 32 } && token.All(char.IsAsciiHexDigit);
    public static bool TryParse(string text, out string token, out long sequence)
    {
        token = ""; sequence = 0;
        const string prefix = "agwinterm;command-finished;";
        if (!text.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var parts = text[prefix.Length..].Split(';');
        if (parts.Length != 2 || !ValidToken(parts[0]) || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out sequence) || sequence <= 0) return false;
        token = parts[0]; return true;
    }
    public static long Read(string path)
    {
        if (string.IsNullOrEmpty(path)) return 0;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > 20) return 0;
            using var reader = new StreamReader(file);
            return long.TryParse(reader.ReadToEnd(), NumberStyles.None, CultureInfo.InvariantCulture, out long value) && value > 0 ? value : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
    public long Checkpoint => Math.Max(Volatile.Read(ref _observed), Read(_path));
    public bool Accept(string token, long sequence, bool persist)
    {
        if (!ValidToken(Token) || token != Token || sequence <= 0) return false;
        lock (_gate)
        {
            if (sequence <= _observed || sequence < Read(_path)) return false;
            // The host may have recorded this same prompt before forwarding it to the UI.
            _observed = sequence;
            if (persist && sequence > Read(_path))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    string temporary = _path + ".tmp";
                    File.WriteAllText(temporary, sequence.ToString(CultureInfo.InvariantCulture));
                    File.Move(temporary, _path, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Trace.WriteLine("Shell completion checkpoint: " + ex.Message); }
            }
            return true;
        }
    }
}

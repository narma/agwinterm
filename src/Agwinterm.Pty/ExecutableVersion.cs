using System.Diagnostics;

namespace Agwinterm.Pty;

public static class ExecutableVersion
{
    private static string? Normalize(string? version) => string.IsNullOrWhiteSpace(version) ? null : version.Split('+')[0].Trim();
    public static string Compare(string? running, string? disk)
    {
        running = Normalize(running); disk = Normalize(disk);
        if (running is null || disk is null || running == "dev" || disk == "dev") return "unknown";
        return running == disk ? "match" : "different";
    }
    public static object Read(string running, string path, string channel)
    {
        string? disk = null; string availability;
        try { if (!File.Exists(path)) availability = "missing-file"; else { disk = Normalize(FileVersionInfo.GetVersionInfo(path).ProductVersion); availability = disk is null ? "metadata-unavailable" : "available"; } }
        catch { availability = "metadata-unavailable"; }
        return new { running, path, channel, onDisk = disk, availability, comparison = Compare(running, disk), source = "running-executable-path" };
    }
}

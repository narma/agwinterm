using System.Text.Json;

namespace Agwinterm.Pty.Tests;

public sealed class StartupJournalTests
{
    private static string[] ReadShared(string path)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
    [Fact]
    public void RecordsAreReadableWhileRunningAndRetentionPreservesOtherLogs()
    {
        string root = Path.Combine(Path.GetTempPath(), "startup-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "other.log"), "keep");
            for (int i = 0; i < 15; i++) File.WriteAllText(Path.Combine(root, $"startup-gui-20000101-{i:D2}.jsonl"), "old");
            using var journal = new StartupJournal(root, "gui");
            using (journal.Measure("restore", "pane-id")) { }
            var files = Directory.GetFiles(root, "startup-gui-*.jsonl");
            Assert.Equal(10, files.Length);
            Assert.True(File.Exists(Path.Combine(root, "other.log")));
            var lines = ReadShared(files.OrderByDescending(Path.GetFileName).First());
            Assert.Equal(3, lines.Length);
            using var record = JsonDocument.Parse(lines[2]);
            Assert.Equal("restore.end", record.RootElement.GetProperty("stage").GetString());
            Assert.True(record.RootElement.GetProperty("durationMs").GetDouble() >= 0);
            for (int i = 0; i < 5000; i++) journal.Mark(new string('x', 2000));
            Assert.True(new FileInfo(files.OrderByDescending(Path.GetFileName).First()).Length < 1024 * 1024 + 4096);
            Assert.True(ReadShared(files.OrderByDescending(Path.GetFileName).First()).Length <= 4096);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void UnwritableDestinationDoesNotBreakStartup()
    {
        string file = Path.GetTempFileName();
        try
        {
            using var journal = new StartupJournal(file, "gui");
            using (journal.Measure("restore")) journal.Mark("ready");
        }
        finally { File.Delete(file); }
    }
}

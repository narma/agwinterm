using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public sealed class ActivityJournalTests
{
    [Fact]
    public async Task QueuedWritesDrainInOrderAtShutdown()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var journal = new ActivityJournal(dir.FullName);
            for (int i = 0; i < 100; i++)
                journal.Enqueue("GUI", ActivityLevel.Info, i.ToString());

            await journal.CompleteWrites().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(Enumerable.Range(0, 100).Reverse().Select(i => i.ToString()),
                new ActivityJournal(dir.FullName).Read().Select(entry => entry.Message));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void RotatedEntriesSurviveRestartInNewestFirstOrder()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var writer = new ActivityJournal(dir.FullName);
            for (int i = 0; i < 90; i++)
                writer.Write("Host", ActivityLevel.Info, i.ToString(), new string('a', 8192));
            Parallel.For(0, 12, i => new ActivityJournal(dir.FullName).Write("GUI", ActivityLevel.Warning, $"toast-{i}"));
            writer.Write("WSL", ActivityLevel.Error, "Rename failed", "wsl.exe, exit 2\nsyntax error");
            var saved = new ActivityJournal(dir.FullName).Read();
            Assert.True(File.Exists(Path.Combine(dir.FullName, "activity.jsonl.1")));
            Assert.Equal(103, saved.Count);
            Assert.Contains("wsl.exe, exit 2", saved[0].FullText);
            Assert.Equal(ActivityLevel.Error, saved[0].Level);
            Assert.Equal("0", saved[^1].Message);
        }
        finally { dir.Delete(true); }
    }
}

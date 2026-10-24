using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class SessionTitleTests
{
    [Theory]
    [InlineData(null, "Fix login redirect", "Fix login redirect")]
    [InlineData(null, "Проверка хуков", "Проверка хуков")]
    [InlineData("My task", "Agent's generated title", "My task")]
    [InlineData(null, "", "session 1")]
    [InlineData(null, "  ", "session 1")]
    [InlineData(null, "C:\\Windows\\System32\\cmd.exe", "session 1")]
    [InlineData(null, "C:\\Projects\\example", "session 1")]
    public void DisplayUsesTaskTitleUnlessManuallyNamed(string? custom, string title, string expected)
        => Assert.Equal(expected, SessionNames.Display(custom, title, "session 1"));

    [Fact]
    public void ClearingProgramTitleRestoresFallback()
    {
        Assert.Equal("Task A", SessionNames.Display(null, "Task A", "session 1"));
        Assert.Equal("Task B", SessionNames.Display(null, "Task B", "session 1"));
        Assert.Equal("session 1", SessionNames.Display(null, "", "session 1"));
    }
}

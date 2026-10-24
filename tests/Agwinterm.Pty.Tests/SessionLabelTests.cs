using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class SessionLabelTests
{
    [Theory]
    [InlineData(null, "Fix login bug", "Fix login bug")]
    [InlineData("Pinned", "New task", "Pinned")]
    [InlineData(null, "", "project")]
    [InlineData(null, "C:\\Windows\\pwsh.exe", "project")]
    [InlineData(null, "PWSH.EXE", "project")]
    [InlineData(null, "/bin/zsh", "project")]
    [InlineData(null, "file:///home/me", "project")]
    [InlineData(null, "bad\nlabel", "project")]
    public void ProgramTitleIsSharedAndManualNameWins(string? custom, string title, string expected)
        => Assert.Equal(expected, SessionNames.DisplayName(custom, "/work/project", title));

    [Theory]
    [InlineData(null, null, "~")]
    [InlineData(null, "", "~")]
    [InlineData(null, "/", "/")]
    [InlineData(null, "/a/b/", "b")]
    [InlineData(null, "/home/проект с пробелами", "проект с пробелами")]
    [InlineData(null, "C:\\", "C:\\")]
    [InlineData(null, "C:/", "C:\\")]
    [InlineData(null, "C:\\work\\project\\", "project")]
    [InlineData(null, "\\\\server\\share\\folder\\", "folder")]
    [InlineData(null, "\\\\server\\share\\", "share")]
    [InlineData("  Task name  ", "/a/b", "Task name")]
    [InlineData("session 1", "/a/b", "session 1")]
    [InlineData(" ", "/a/b", "b")]
    public void LabelUsesExplicitNameOrPortableBasename(string? custom, string? cwd, string expected)
        => Assert.Equal(expected, SessionNames.DisplayName(custom, cwd));
}

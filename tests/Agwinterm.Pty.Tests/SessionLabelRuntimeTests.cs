using System.Collections;
using System.Reflection;
using System.Text;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class SessionLabelRuntimeTests
{
    private static Type ProgramType()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "src", "Agwinterm.Win32"))) root = Path.GetDirectoryName(root);
        var path = Directory.GetFiles(Path.Combine(root!, "src", "Agwinterm.Win32", "bin"), "Agwinterm.Win32.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).First();
        return Assembly.LoadFrom(path).GetType("Agwinterm.Win32.Program", true)!;
    }

    private static void Set(object value, string field, object? data) => value.GetType().GetField(field)!.SetValue(value, data);

    private static (object session, object pane, Func<string> name) Make(Type program, TerminalSession terminal, bool sidebar = false)
    {
        object session = Activator.CreateInstance(program.GetNestedType("Ses", BindingFlags.NonPublic)!, true)!;
        object pane = Activator.CreateInstance(program.GetNestedType("Pane", BindingFlags.NonPublic)!, true)!;
        Set(pane, "S", terminal); Set(pane, "StartCwd", "/work/project");
        ((IList)session.GetType().GetField("Panes")!.GetValue(session)!).Add(pane);
        return (session, pane, () => (string)session.GetType().GetProperty(sidebar ? "SidebarName" : "Name")!.GetValue(session)!);
    }

    [Fact]
    public void HandoffTitleIsReplaceableProgramStateNotPinnedName()
    {
        using var terminal = new TerminalSession(80, 24);
        var program = ProgramType();
        var (session, _, name) = Make(program, terminal);
        program.GetMethod("SeedHandoffTitle", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [terminal, "Startup task"]);
        Assert.Equal("Startup task", name());
        Assert.Null(session.GetType().GetField("CustomName")!.GetValue(session));
        terminal.Emulator.Feed(Encoding.UTF8.GetBytes("\x1b]2;New task\x07"));
        Assert.Equal("New task", name());
        Set(session, "CustomName", "Pinned");
        Assert.Equal("Pinned", name());
        Set(session, "CustomName", null);
        terminal.Emulator.Feed(Encoding.UTF8.GetBytes("\x1b]2;\x07\x1b]7;file://localhost/work/next\x07"));
        Assert.Equal("next", name());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task BusyPaneCannotBlockPinnedOrAutomaticLabel(bool pinned, bool cached)
    {
        using var terminal = new TerminalSession(80, 24);
        var (session, _, name) = Make(ProgramType(), terminal, sidebar: true);
        terminal.Emulator.Feed(Encoding.UTF8.GetBytes("\x1b]2;Cached task\x07"));
        if (cached) Assert.Equal("Cached task", name());
        if (pinned) Set(session, "CustomName", "Pinned");
        using var locked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writer = Task.Run(() => { lock (terminal.SyncRoot) { locked.Set(); release.Wait(); } });
        Assert.True(locked.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            var read = Task.Run(name);
            string label = await read.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(pinned ? "Pinned" : cached ? "Cached task" : "project", label);
        }
        finally { release.Set(); await writer; }
        if (!pinned) Assert.Equal("Cached task", name());
    }
}

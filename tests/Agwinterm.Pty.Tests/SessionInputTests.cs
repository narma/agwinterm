using Agwinterm.Core;
using System.Reflection;

namespace Agwinterm.Pty.Tests;

public class SessionInputTests
{
    [Theory]
    [InlineData("focus")] [InlineData("resize")] [InlineData("command")]
    public async Task QueuedTargetRemovedBeforeExecutionNeverFallsBack(string operation)
    {
        // A message-only HWND accepts the production PostMessage, but has no app
        // message pump: the test controls exactly when the real queued action runs.
        nint hwnd = CreateWindowExW(0, "STATIC", "C0 queue test", 0, 0, 0, 0, 0, new nint(-3), 0, 0, 0);
        Assert.NotEqual(nint.Zero, hwnd);
        using var gone = new CancellationTokenSource();
        using var input = new InputSession();
        try
        {
            var program = LoadProgram();
            var app = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(program);
            object Make(string name) => Activator.CreateInstance(program.GetNestedType(name, BindingFlags.NonPublic)!, true)!;
            static FieldInfo Field(object o, string n) => o.GetType().GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
            static void Set(object o, string n, object v) => Field(o, n).SetValue(o, v);
            static System.Collections.IList List(object o, string n) => (System.Collections.IList)Field(o, n).GetValue(o)!;
            var ws = Make("Workspace"); var source = Make("Ses"); var visible = Make("Ses"); var pane = Make("Pane");
            Set(source, "Id", "removed"); Set(source, "Ws", ws); Set(pane, "Id", "removed"); Set(pane, "S", input);
            List(source, "Panes").Add(pane); List(ws, "Sessions").Add(source);
            var visiblePane = Make("Pane"); Set(visiblePane, "Id", "visible"); Set(visiblePane, "S", input);
            List(visible, "Panes").Add(visiblePane);
            Set(app, "_active", visible); Set(app, "_hwnd", hwnd); Set(app, "_uiGone", gone);
            Set(app, "_workspaces", Activator.CreateInstance(Field(app, "_workspaces").FieldType)!);
            List(app, "_workspaces").Add(ws);
            object queue = Activator.CreateInstance(Field(app, "_uiActions").FieldType)!;
            Set(app, "_uiActions", queue);
            var host = (ISessionHost)app;
            var request = Task.Run(() => operation switch
            {
                "focus" => host.FocusPaneDir("other", "removed"),
                "resize" => host.ResizeSplit(0.3, 0, 0, 0, 0, "removed"),
                _ => host.CommandRun("echo forbidden", "send", "removed"),
            });
            Assert.True(SpinWait.SpinUntil(() => (int)queue.GetType().GetProperty("Count")!.GetValue(queue)! > 0, 3000));
            List(ws, "Sessions").Clear();
            object?[] slot = [null];
            Assert.True((bool)queue.GetType().GetMethod("TryDequeue")!.Invoke(queue, slot)!);
            slot[0]!.GetType().GetMethod("RunIfQueued")!.Invoke(slot[0], null);
            // Destroy on its creating thread before an await may resume elsewhere.
            Assert.True(DestroyWindow(hwnd)); hwnd = 0;
            string result = await request.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.StartsWith(ISessionHost.RefusePrefix, result);
            Assert.Contains("unknown or ambiguous target", result);
            Assert.Same(visible, Field(app, "_active").GetValue(app));
            Assert.Equal(0, input.Attempts);
        }
        finally { gone.Cancel(); if (hwnd != 0) DestroyWindow(hwnd); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint parameter);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ExplicitCommandUsesBackgroundPaneAndNeverBroadcasts(int protection)
    {
        using var visible = new InputSession();
        using var target = new InputSession { InputClosed = protection == 2, FailWrite = protection == 3 };
        var program = LoadProgram();
        var app = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(program);
        object Make(string name) => Activator.CreateInstance(program.GetNestedType(name, BindingFlags.NonPublic)!, true)!;
        static void Set(object o, string n, object v) => o.GetType().GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(o, v);
        static System.Collections.IList List(object o, string n) => (System.Collections.IList)o.GetType().GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(o)!;
        var ws = Make("Workspace"); Set(ws, "Name", "target-work");
        var background = Make("Ses"); Set(background, "Id", "background"); Set(background, "CustomName", "Background"); Set(background, "Ws", ws);
        var shown = Make("Ses"); Set(shown, "Id", "shown"); Set(shown, "Ws", ws);
        var p = Make("Pane"); Set(p, "Id", "background"); Set(p, "S", target); Set(p, "ReadOnly", protection == 1); Set(p, "StartCwd", @"C:\target");
        var other = Make("Pane"); Set(other, "Id", "background-other"); Set(other, "S", visible);
        List(background, "Panes").Add(p); List(shown, "Panes").Add(other);
        List(ws, "Sessions").Add(background); List(ws, "Sessions").Add(shown);
        Set(app, "_workspaces", Activator.CreateInstance(program.GetField("_workspaces", BindingFlags.NonPublic | BindingFlags.Instance)!.FieldType)!);
        List(app, "_workspaces").Add(ws);
        Set(app, "_active", shown); Set(app, "_session", visible); Set(app, "_broadcast", true);
        var run = program.GetMethod("RunCommandText", BindingFlags.NonPublic | BindingFlags.Instance)!;
        string result = (string)run.Invoke(app, ["{AGW_SESSION_ID}|{AGW_PANE_ID}|{AGW_WORKSPACE}|{AGW_CWD}", "send", "background"])!;
        Assert.Equal(0, visible.Attempts);
        Assert.Same(shown, program.GetField("_active", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(app));
        Assert.Equal(protection != 0, result.StartsWith(ISessionHost.RefusePrefix, StringComparison.Ordinal));
        if (protection == 0) Assert.Equal("background|background|target-work|C:\\target\r", target.Input);
        if (protection == 3) Assert.Contains("do not retry blindly", result);
        int attempts = target.Attempts;
        foreach (string invalid in new[] { "missing", "", " ", "back" })
            Assert.StartsWith(ISessionHost.RefusePrefix, (string)run.Invoke(app, ["bad", "send", invalid])!);
        Assert.StartsWith(ISessionHost.RefusePrefix, (string)run.Invoke(app, ["bad", "typo", "background"])!);
        Assert.Equal(attempts, target.Attempts);
        Set(other, "Id", "shown");
        List(ws, "Sessions").Remove(background);
        Assert.StartsWith(ISessionHost.RefusePrefix, (string)run.Invoke(app, ["bad", "send", "background"])!);
        Assert.Equal(0, visible.Attempts);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void ActualSendBroadcastDoesNotLetActiveProtectionBlockSibling(bool closed, bool command)
    {
        using var active = new InputSession { InputClosed = closed };
        using var live = new InputSession();
        var program = LoadProgram();
        // No Program constructor: no HWND, host, timers, or shared state is created.
        var app = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(program);
        object Make(string nested) => Activator.CreateInstance(program.GetNestedType(nested, BindingFlags.NonPublic)!, true)!;
        static void Set(object o, string name, object value) => o.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(o, value);
        static System.Collections.IList List(object o, string name) => (System.Collections.IList)o.GetType().GetField(name)!.GetValue(o)!;
        var ws = Make("Workspace"); var ses = Make("Ses");
        var pane = Make("Pane"); var sibling = Make("Pane");
        Set(pane, "S", active); Set(pane, "ReadOnly", !closed); Set(pane, "HasSel", true); Set(pane, "ScrollOffset", 5);
        Set(sibling, "S", live); Set(ses, "Ws", ws);
        List(ses, "Panes").Add(pane); List(ses, "Panes").Add(sibling); List(ws, "Sessions").Add(ses);
        Set(app, "_active", ses); Set(app, "_session", active); Set(app, "_broadcast", true);
        Set(app, "_workspaces", Activator.CreateInstance(program.GetField("_workspaces", BindingFlags.NonPublic | BindingFlags.Instance)!.FieldType)!);
        var method = program.GetMethod(command ? "RunCommandText" : "Send", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = method.Invoke(app, command ? ["payload", "send", null] : ["payload", true]);
        if (command) Assert.Contains("do not retry blindly", Assert.IsType<string>(result));
        else Assert.False(Assert.IsType<bool>(result));
        Assert.Equal(0, active.Attempts); Assert.Equal(command ? "payload\r" : "payload", live.Input);
        Assert.True((bool)pane.GetType().GetField("HasSel")!.GetValue(pane)!);
        Assert.Equal(5, pane.GetType().GetField("ScrollOffset")!.GetValue(pane));
    }

    [Fact]
    public void BroadcastContinuesPastClosedProtectedAndFailingPanes()
    {
        using var closed = new InputSession { InputClosed = true };
        using var failing = new InputSession { FailWrite = true };
        using var protectedPane = new InputSession(); using var live = new InputSession();
        Assert.False(SessionInput.Broadcast([(closed, false), (failing, false), (protectedPane, true), (live, false)], "payload"u8));
        Assert.Equal(0, closed.Attempts); Assert.Equal(1, failing.Attempts); Assert.Equal(0, protectedPane.Attempts);
        Assert.Equal("payload", live.Input); Assert.Equal(1, live.Attempts);
    }

    [Theory]
    [InlineData(false, 0, false)] [InlineData(true, 0, false)]
    [InlineData(false, 2, false)] [InlineData(true, 2, false)]
    [InlineData(false, 0, true)] [InlineData(true, 0, true)]
    [InlineData(false, 1, true)] [InlineData(true, 1, true)]
    [InlineData(false, 2, true)] [InlineData(true, 2, true)]
    public void ActualSendKeepsCoversTargetedWhileBroadcastIsOn(bool paneOverlay, int state, bool command)
    {
        using var shell = new InputSession(); using var cover = new InputSession { InputClosed = state == 2 };
        var program = LoadProgram();
        var app = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(program);
        object Make(string name) => Activator.CreateInstance(program.GetNestedType(name, BindingFlags.NonPublic)!, true)!;
        static void Set(object o, string name, object value) => o.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(o, value);
        var ses = Make("Ses"); var pane = Make("Pane"); var overlay = Make("Pane");
        Set(pane, "S", shell); Set(overlay, "S", cover); Set(overlay, "ReadOnly", state == 1);
        ((System.Collections.IList)ses.GetType().GetField("Panes")!.GetValue(ses)!).Add(pane);
        if (paneOverlay) Set(pane.GetType().GetField("Overlay")!.GetValue(pane)!, "Term", overlay);
        else Set(app, "_cover", overlay);
        Set(app, "_active", ses); Set(app, "_session", cover); Set(app, "_broadcast", true);
        Assert.False((bool)program.GetProperty("BroadcastActive", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(app)!);
        var result = program.GetMethod(command ? "RunCommandText" : "Send", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(app, command ? ["payload", "send", null] : ["payload", true]);
        Assert.Equal(0, shell.Attempts);
        Assert.Equal(state == 0 ? (command ? "payload\r" : "payload") : "", cover.Input);
        if (command) Assert.Equal(state != 0, Assert.IsType<string>(result).StartsWith(ISessionHost.RefusePrefix, StringComparison.Ordinal));
        else Assert.Equal(state == 0, Assert.IsType<bool>(result));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ActualPaneHostQueryReplyCannotAbortFollowingOutput(bool closed)
    {
        using var session = new InputSession { InputClosed = closed, FailWrite = true };
        var type = LoadProgram().GetNestedType("PaneHost", BindingFlags.NonPublic)!;
        session.Emulator.Host = (IHostActions)Activator.CreateInstance(type, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, [null, null, session], null)!;
        // Kitty flags query requests a real host reply; the marker follows in the SAME Feed call.
        session.Emulator.Feed("\x1b[?uFINAL-after-query"u8);
        Assert.Contains("FINAL-after-query", session.Emulator.DumpRow(0));
        Assert.Equal(closed ? 0 : 1, session.Attempts);
    }

    private static Type LoadProgram()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "src", "Agwinterm.Win32"))) root = Path.GetDirectoryName(root);
        var path = Directory.GetFiles(Path.Combine(root!, "src", "Agwinterm.Win32", "bin"), "Agwinterm.Win32.dll", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).First();
        return Assembly.LoadFrom(path).GetType("Agwinterm.Win32.Program", true)!;
    }

    private sealed class InputSession : ISession
    {
        public bool InputClosed { get; init; }
        public bool FailWrite; public int Attempts; public string Input = "";
        public ITerminalCore Emulator { get; } = new TerminalEmulator(80, 24);
        public int Cols => 80; public int Rows => 24; public int? ChildProcessId => null;
        public object SyncRoot { get; } = new();
        public AgentStatus Status => AgentStatus.Idle; public bool Blink => false; public bool AutoReset => false;
        public long StatusChangedAt => 0; public bool HasExited => false; public int? ExitCode => null;
        public event Action? OutputReceived { add { } remove { } }
        public event Action? StatusChanged { add { } remove { } }
        public event Action<string?>? SoundRequested { add { } remove { } }
        public event Action<int>? Exited { add { } remove { } }
        public void NotifyActivity() { }
        public void SetStatus(AgentStatus status, bool blink = false, bool autoReset = false, bool sound = false, string? soundName = null) { }
        public void Write(ReadOnlySpan<byte> bytes) { Attempts++; if (FailWrite) throw new IOException("closed during write"); Input += System.Text.Encoding.UTF8.GetString(bytes); }
        public Task StartAsync(string app, string[] commandLine, bool verbatimCommandLine = false, IReadOnlyDictionary<string, string>? extraEnv = null, string? cwd = null, bool deElevate = false, bool freshEnv = true, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> RunAsync(string app, string[] commandLine, bool verbatimCommandLine = false, CancellationToken ct = default) => throw new NotSupportedException();
        public void Attach(Microsoft.Win32.SafeHandles.SafeFileHandle conOut, Microsoft.Win32.SafeHandles.SafeFileHandle conIn, Microsoft.Win32.SafeHandles.SafeFileHandle signal, IntPtr clientProcess, int pid) => throw new NotSupportedException();
        public void Inject(ReadOnlySpan<byte> bytes) => Emulator.Feed(bytes);
        public void MutateLocked(Action<ITerminalCore> mutate) => mutate(Emulator);
        public void Resize(int cols, int rows) => Emulator.Resize(cols, rows);
        public string SnapshotRow(int row) => Emulator.DumpRow(row);
        public (int Row, int Col) SnapshotCursor() => (Emulator.CursorRow, Emulator.CursorCol);
        public void Detach() { } public void Dispose() { }
    }
}

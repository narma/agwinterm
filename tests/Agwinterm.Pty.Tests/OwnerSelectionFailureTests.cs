using System.Reflection;
using System.Text.Json;
using Agwinterm.Core;
using Xunit;

namespace Agwinterm.Pty.Tests;

public class OwnerSelectionFailureTests
{
    [Theory]
    [InlineData("stale", false, "Terminal content changed")]
    [InlineData("limit", false, "exceeds the selection limit")]
    [InlineData("closed", false, "Could not read command output")]
    [InlineData("io-error", false, "Could not read command output")]
    [InlineData("malformed", false, "Could not read command output")]
    [InlineData("ok", true, "exceeds the selection limit")]
    [InlineData("ok", false, null)]
    [InlineData("no-marks", false, null)]
    public async Task CreationRetainsFailureWithoutMistakingItForMissingShellMarks(string status, bool truncated, string? error)
    {
        using var initial = Frame(7, "hi");
        using var frame = initial.Acquire();
        using var selection = CreateSelection(new ReplyPresentation(status, truncated), frame);
        await Ready(selection);
        var creationError = selection.GetType().GetProperty("CreationError")!;
        string? actual = (string?)creationError.GetValue(selection);
        if(error is null) Assert.Null(actual);
        else Assert.Contains(error,actual);
        // Later locate replies must not erase the original creation failure.
        using var updated = Frame(8, "hi");
        using var updatedFrame = updated.Acquire();
        Locate(selection, updatedFrame);
        Assert.Equal(actual, creationError.GetValue(selection));
    }

    [Fact]
    public async Task PendingLocateKeepsUnchangedHighlightAndHidesItWhenSelectedContentChanges()
    {
        var presentation = new ReplyPresentation("ok", false, delayLocate: true);
        using var initial = Frame(7, "hi!");
        using var first = initial.Acquire();
        using var selection = CreateSelection(presentation, first);
        await Ready(selection);

        using var unrelatedChange = Frame(8, "hi?");
        using var second = unrelatedChange.Acquire();
        var visible = Assert.IsType<TerminalSelectionResult>(Locate(selection, second));
        Assert.True(visible.Valid);
        Assert.Equal((0, 0, 0, 1), (visible.AnchorRow, visible.AnchorColumn, visible.FocusRow, visible.FocusColumn));
        Assert.Equal(1, presentation.LocateRequests);

        using var selectedChange = Frame(9, "ho?");
        using var third = selectedChange.Acquire();
        Assert.Null(Locate(selection, third));
        Assert.Equal(1, presentation.LocateRequests);
        Assert.Equal("hi", selection.GetType().GetProperty("Text")!.GetValue(selection));
    }

    private static TerminalFrame Frame(long generation, string text) =>
        new(new("test", 1, 1, (ulong)generation, generation),
            new(text.Length, 1, 0, 0, 0, 0, 0, true, 0, false, false, default, default, "", ""),
            text.Select(ch => Cell.Empty with { Rune = ch }).ToArray());

    private static IDisposable CreateSelection(ISessionPresentation presentation, TerminalFrame.Lease frame)
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "src", "Agwinterm.Win32"))) root = Path.GetDirectoryName(root);
        string path = Directory.GetFiles(Path.Combine(root!, "src", "Agwinterm.Win32", "bin"), "Agwinterm.Win32.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).First();
        Type type = Assembly.LoadFrom(path).GetType("Agwinterm.Win32.Program", true)!.GetNestedType("OwnerSelection", BindingFlags.NonPublic)!;
        return (IDisposable)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [presentation, frame, new TerminalSelectionRequest(Guid.NewGuid().ToString("N"), 0, 0, 0, 1, false), null, (Action)(() => { })], null)!;
    }

    private static Task Ready(IDisposable selection) => (Task)selection.GetType().GetProperty("Ready")!.GetValue(selection)!;

    private static TerminalSelectionResult? Locate(IDisposable selection, TerminalFrame.Lease frame) =>
        (TerminalSelectionResult?)selection.GetType().GetMethod("Locate")!.Invoke(selection, [frame]);

    private sealed class ReplyPresentation(string status, bool truncated, bool delayLocate = false) : ISessionPresentation
    {
        private readonly TaskCompletionSource<TerminalQueryResult> _pendingLocate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int LocateRequests { get; private set; }
        public bool IsRemote => true;
        public TerminalFrame.Lease? Capture(int scrollOffset,int cellWidth,int cellHeight) => null;
        public TerminalInputAcceptance Submit(TerminalInput input) => TerminalInputAcceptance.Accepted;
        public TerminalInputAcceptance Execute(TerminalCommand command) => TerminalInputAcceptance.Accepted;
        public ValueTask<TerminalQueryResult> QueryAsync(TerminalQuery query,CancellationToken cancellationToken)
        {
            if (query.Operation == "selection-locate" && delayLocate)
            {
                LocateRequests++;
                return new(_pendingLocate.Task.WaitAsync(cancellationToken));
            }
            if(status == "io-error" && query.Operation == "selection-create") throw new IOException("Disconnected");
            string replyStatus=query.Operation == "selection-create" ? status : "ok";
            string json=status == "malformed" ? "{" : JsonSerializer.Serialize(new TerminalSelectionResult("test",status != "no-marks",0,0,0,1,status == "no-marks" ? null : "hi"));
            return ValueTask.FromResult(new TerminalQueryResult("test",1,7,
                replyStatus is "no-marks" or "malformed" ? "ok" : replyStatus,[json],truncated));
        }
    }
}

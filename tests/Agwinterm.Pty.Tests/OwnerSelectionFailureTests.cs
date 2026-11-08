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
        string? root=AppContext.BaseDirectory;
        while(root is not null && !Directory.Exists(Path.Combine(root,"src","Agwinterm.Win32"))) root=Path.GetDirectoryName(root);
        string path=Directory.GetFiles(Path.Combine(root!,"src","Agwinterm.Win32","bin"),"Agwinterm.Win32.dll",SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).First();
        Type type=Assembly.LoadFrom(path).GetType("Agwinterm.Win32.Program",true)!.GetNestedType("OwnerSelection",BindingFlags.NonPublic)!;
        var presentation=new ReplyPresentation(status,truncated);
        using var selection=(IDisposable)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,
            [presentation,new TerminalFrameIdentity("test",1,1,1,7),new TerminalSelectionRequest(Guid.NewGuid().ToString("N"),0,0,0,1,false),null,(Action)(()=>{})],null)!;
        await (Task)type.GetProperty("Ready")!.GetValue(selection)!;
        string? actual=(string?)type.GetProperty("CreationError")!.GetValue(selection);
        if(error is null) Assert.Null(actual);
        else Assert.Contains(error,actual);
        // Later locate replies must not erase the original creation failure.
        type.GetMethod("Locate")!.Invoke(selection,[8L]);
        Assert.Equal(actual,type.GetProperty("CreationError")!.GetValue(selection));
    }

    private sealed class ReplyPresentation(string status,bool truncated) : ISessionPresentation
    {
        public bool IsRemote => true;
        public TerminalFrame.Lease? Capture(int scrollOffset,int cellWidth,int cellHeight) => null;
        public TerminalInputAcceptance Submit(TerminalInput input) => TerminalInputAcceptance.Accepted;
        public TerminalInputAcceptance Execute(TerminalCommand command) => TerminalInputAcceptance.Accepted;
        public ValueTask<TerminalQueryResult> QueryAsync(TerminalQuery query,CancellationToken cancellationToken)
        {
            if(status == "io-error" && query.Operation == "selection-create") throw new IOException("Disconnected");
            string replyStatus=query.Operation == "selection-create" ? status : "ok";
            string json=status == "malformed" ? "{" : JsonSerializer.Serialize(new TerminalSelectionResult("test",status != "no-marks",0,0,0,1,status == "no-marks" ? null : "hi"));
            return ValueTask.FromResult(new TerminalQueryResult("test",1,7,
                replyStatus is "no-marks" or "malformed" ? "ok" : replyStatus,[json],truncated));
        }
    }
}

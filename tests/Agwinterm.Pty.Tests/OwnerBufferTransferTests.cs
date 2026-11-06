using Agwinterm.Core;
using Agwinterm.Pty.Proto;

namespace Agwinterm.Pty.Tests;

public class OwnerBufferTransferTests
{
    [Fact]
    public void PartialAndRepeatedImportsCannotMutateOrDuplicateHistory()
    {
        using var session=new TerminalSession(40,4,"managed");
        session.Inject("LIVE"u8);
        var transfer=new OwnerBufferTransfer(session);
        byte[] bytes=BufferPersist.FromPlainText(["SAVED"],40);
        var begin=Run("buffer-import",bytes.Length);
        string token=Assert.Single(begin.Rows);
        Assert.Equal("invalid",Run("buffer-commit",text:token).Status);
        Assert.Equal(0,session.Emulator.HistoryCount);
        Assert.Equal("invalid",Run("buffer-write",1,token+":"+Convert.ToBase64String(bytes)).Status);
        Assert.Equal("ok",Run("buffer-write",0,token+":"+Convert.ToBase64String(bytes)).Status);
        Assert.Equal("ok",Run("buffer-commit",text:token).Status);
        int rows=session.Emulator.HistoryCount;
        Assert.True(rows > 0);
        Assert.Equal("invalid",Run("buffer-commit",text:token).Status);
        Assert.Equal(rows,session.Emulator.HistoryCount);
        Assert.StartsWith("LIVE",session.Emulator.DumpRow(0));
        PresentationQueryReply Run(string operation,int offset=0,string text="")
        {
            var reply=new PresentationQueryReply();
            transfer.Execute(new() {Operation=operation,StartRow=offset,Text=text},reply);
            return reply;
        }
    }

    [Fact]
    public void ExportIsImmutableAndTokensFenceSupersededTransfers()
    {
        using var session=new TerminalSession(40,4,"managed");
        session.Inject("BEFORE"u8);
        var transfer=new OwnerBufferTransfer(session);
        var first=Run("buffer-export"); string oldToken=first.Rows[0];
        session.Inject("AFTER"u8);
        var read=Run("buffer-read",oldToken);
        Assert.True(BufferPersist.TryParse(Convert.FromBase64String(Assert.Single(read.Rows)),out var saved));
        Assert.Equal("BEFORE",string.Concat(saved.Rows[0].Cells.Select(c=>char.ConvertFromUtf32(c.Rune))));
        Run("buffer-export");
        Assert.Equal("invalid",Run("buffer-read",oldToken).Status);
        PresentationQueryReply Run(string operation,string text="")
        {
            var reply=new PresentationQueryReply(); transfer.Execute(new(){Operation=operation,Text=text},reply); return reply;
        }
    }
}

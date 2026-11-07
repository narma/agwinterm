using System.IO;
using System.Text;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// Diagnostic: determines whether ConPTY forwards a Kitty graphics APC sequence
/// emitted by a child process, or strips it. The outcome decides the docxy
/// integration approach (inline-via-ConPTY vs. control-API injection).
/// </summary>
public class ConPtyGraphicsPassthroughTests
{
    [Fact]
    public async Task ConPty_ForwardsKittyApc_FromChildProcess()
    {
        // Build a minimal Kitty APC: 1x1 RGB image, payload bytes {1,2,3} -> base64 "AQID".
        //   ESC _ G a=T,i=1,f=24,s=1,v=1 ; AQID ESC \
        var apc = new List<byte> { 0x1b };
        apc.AddRange(Encoding.ASCII.GetBytes("_Ga=T,i=1,f=24,s=1,v=1;AQID"));
        apc.Add(0x1b);
        apc.Add((byte)'\\');

        string file = Path.Combine(Path.GetTempPath(), "agw_conpty_apc.bin");
        File.WriteAllBytes(file, apc.ToArray());

        using var session = new TerminalSession(80, 24);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // `cmd /c type <file>` emits the raw bytes (all <= 0x7f, no Ctrl-Z) to stdout.
        await session.RunAsync("cmd.exe", new[] { "/c", "type", file }, verbatimCommandLine: true, cts.Token);

        var core = (Agwinterm.Core.GhosttyTerminalCore)session.Emulator;
        core.BeginPaint(); core.CompletePaint();
        int images = core.PaintImages.Count;

        // The bundled ConPTY forwards Kitty graphics to the native terminal parser.
        Assert.Equal(1, images);
        Assert.Equal(1u, core.PaintImages[0].Id);
        Assert.Equal(new byte[] { 3, 2, 1, 255 }, core.PaintImages[0].Image.ToPremultipliedBgra());
    }
}

using System.Buffers.Binary;
using System.Globalization;
using Porta.Pty;

namespace Agwinterm.Pty;

/// <summary>A redirected WSL launcher. Only the Linux child owns a terminal.</summary>
internal sealed class WslPtyConnection : IPtyConnection
{
    private readonly IPtyConnection _process;
    private readonly InputFrames _input;
    private WslPtyConnection(IPtyConnection process)
    {
        _process = process;
        _input = new(process.WriterStream);
    }

    internal static WslPtyConnection Spawn(PtyOptions options, bool deElevate = false)
    {
        string[] args = options.VerbatimCommandLine
            ? AgentResume.SplitCommandLine(string.Join(' ', options.CommandLine ?? [])).ToArray()
            : options.CommandLine ?? [];
        int index = WslIntegration.CommandIndex(args);
        var launch = new List<string>();
        string? shellType = null;
        for (int i = 0; i < index; i++)
        {
            if (args[i] == "--shell-type" && i + 1 < index) shellType = args[++i];
            else launch.Add(args[i]);
        }
        launch.AddRange(["--exec", "/bin/sh", "-c", WslIntegration.InstallBootstrap, "agwinterm-pty",
            CliInstaller.CliDir(), "--pty", options.Cols.ToString(CultureInfo.InvariantCulture),
            options.Rows.ToString(CultureInfo.InvariantCulture)]);
        bool direct = shellType == "none" || (index < args.Length && args[index] is "-e" or "--exec");
        if (index < args.Length && args[index] is "--exec" or "-e" or "--") index++;
        if (index == args.Length)
        {
            launch.AddRange(["/bin/sh", "-c", shellType == "standard"
                ? "exec \"${SHELL:-/bin/sh}\"" : "exec \"${SHELL:-/bin/sh}\" -l"]);
        }
        else if (direct) launch.AddRange(args[index..]);
        else
        {
            // The implicit WSL command form uses the distribution shell for
            // variable expansion, redirections and pipelines.
            launch.AddRange(["/bin/sh", "-c", "exec \"${SHELL:-/bin/sh}\" \"$@\"", "agwinterm-shell",
                shellType == "login" ? "-lc" : "-c", string.Join(' ', args.Skip(index).Select(TerminalSession.QuoteArg))]);
        }
        var command = string.Join(' ', launch.Prepend(options.App).Select(TerminalSession.QuoteArg));
        return new(ConPtyConnection.Spawn(null, command, options.Cwd, options.Environment is { Count: > 0 } ? options.Environment : null,
            options.Cols, options.Rows, deElevate));
    }

    public Stream ReaderStream => _process.ReaderStream;
    public Stream WriterStream => _input;
    public int Pid => _process.Pid;
    public int ExitCode => _process.ExitCode;
#pragma warning disable CS0067
    public event EventHandler<PtyExitedEventArgs>? ProcessExited;
#pragma warning restore CS0067
    public bool WaitForExit(int milliseconds) => _process.WaitForExit(milliseconds);
    public void Resize(int cols, int rows)
    {
        try { _input.Resize(cols, rows); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 109 or 232 or 233) { } // peer closed
    }
    public void Kill() => _process.Kill();
    public void Dispose() => _process.Dispose();

    private sealed class InputFrames(Stream pipe) : Stream
    {
        private readonly object _sync = new();
        public override void Write(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty) return;
            lock (_sync)
            {
                Span<byte> header = stackalloc byte[4];
                BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
                pipe.Write(header);
                pipe.Write(data);
            }
        }
        internal void Resize(int cols, int rows)
        {
            Span<byte> frame = stackalloc byte[8];
            frame[..4].Clear();
            BinaryPrimitives.WriteUInt16BigEndian(frame[4..], checked((ushort)cols));
            BinaryPrimitives.WriteUInt16BigEndian(frame[6..], checked((ushort)rows));
            lock (_sync)
            {
                pipe.Write(frame);
                pipe.Flush();
            }
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Flush() { lock (_sync) pipe.Flush(); }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

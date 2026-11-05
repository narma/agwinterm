using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Agwinterm.Core;
using Agwinterm.Pty;

// Compiled fixture; launched only inside the routed VM by the latency suite.
if (args is ["--shutdown-host", var pipe])
{
    if (!System.Text.RegularExpressions.Regex.IsMatch(pipe, "^p13-[0-9a-f]{32}(-presentation-v1)?$"))
        throw new ArgumentException("Only an isolated latency fixture namespace can be shut down.");
    if (!PtyHostClient.IsRunning(pipe)) return;
    using var client = PtyHostClient.Connect(pipe);
    using var host = Process.GetProcessById(checked((int)client.HostPid));
    client.Shutdown();
    if (!host.WaitForExit(15000)) throw new TimeoutException("Fixture host did not exit after shutdown.");
    return;
}
bool bulk=args.Length > 0 && args[0] == "fixed-bulk";
if (!bulk && !TerminalLatencyTrace.Enabled) throw new InvalidOperationException("The echo fixture requires a trace directory.");
nint input = Native.GetStdHandle(-10);
if (!Native.GetConsoleMode(input, out uint previous)) throw new Win32Exception();
if (!Native.SetConsoleMode(input, previous & ~7u)) throw new Win32Exception();
try
{
    Console.OutputEncoding = new UTF8Encoding(false);
    if(bulk)
    {
        int mib=args.Length == 2 && int.TryParse(args[1],out var size) && size is >= 1 and <= 2048 ? size : throw new ArgumentException("Pass fixed-bulk MiB (1..2048).");
        Console.Write("\u001b[?9001l\u001b[?25l\u001b[2J\u001b[HBULK_READY\r\n");
        using var bulkInput=Console.OpenStandardInput();
        while(bulkInput.ReadByte() is var inputByte && inputByte != 13)
            if(inputByte < 0) throw new EndOfStreamException();
        string chunk=string.Concat(Enumerable.Repeat(new string('x',62)+"\r\n",16384));
        for(int i=0;i<mib;i++) Console.Write(chunk);
        Console.Write("\u001b[2J\u001b[HBULK_DONE\r\n"); Console.Out.Flush();
        // Keep the owner and UI alive for the final process CPU/memory sample.
        while(bulkInput.ReadByte() >= 0) { }
        return;
    }
    string workload = args.Length == 0 ? "idle" : args[0];
    if (workload is not ("idle" or "ascii" or "unicode" or "tui" or "history" or "fixed-output")) throw new ArgumentException("Unknown echo workload.");
    Console.Write("\u001b[?9001l\u001b[?25l");
    if (workload == "history")
        for (int line = 0; line < 50000; line++) Console.Write($"HISTORY {line:D6} abcdefghijklmnopqrstuvwxyz 0123456789\r\n");
    Console.Write("\u001b[2J\u001b[HECHO_READY\r\n");
    string? fixedOutput = workload == "fixed-output" ? string.Concat(Enumerable.Range(0,4096).Select(n => $"{n:D6}" + new string('x',56) + "\r\n")) : null;
    if (workload is "ascii" or "unicode" or "tui" or "fixed-output")
    {
        Console.Write("\u001b[3;r\u001b[3;1H");
        string line = workload == "unicode" ? "Сборка 中文 e\u0301 👩🏽‍💻 0123456789\r\n" : "build abcdefghijklmnopqrstuvwxyz 0123456789\r\n";
        string output = workload == "tui" ? "\u001b[3;1H\u001b[32mCPU 0123456789\u001b[0m\u001b[4;1HProcesses running\u001b[K" : string.Concat(Enumerable.Repeat(line, 96));
        if (fixedOutput is null) _ = Task.Run(() => { while (true) Console.Write(output); });
    }
    var marker = new TerminalLatencyTrace.MarkerReader();
    using var stream = Console.OpenStandardInput();
    byte[] buffer = new byte[1];
    while (stream.Read(buffer) == 1)
    {
        long id = marker.Push(buffer[0], true);
        if (id == 0) continue;
        TerminalLatencyTrace.Record(4, id, Stopwatch.GetTimestamp());
        if (fixedOutput is not null) Console.Write(fixedOutput);
        Console.Write($"\u001b7\u001b[2;1H~AGL{id:X8}~\u001b8");
        Console.Out.Flush();
    }
}
finally { Native.SetConsoleMode(input, previous); }

internal static class Native
{
    [DllImport("kernel32.dll")] internal static extern nint GetStdHandle(int handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetConsoleMode(nint handle, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetConsoleMode(nint handle, uint mode);
}

using Agwinterm.Core;
using Agwinterm.Pty;

string? pipe = null;
string? conpty = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--pipe" when i + 1 < args.Length: pipe = args[++i]; break;
        case "--conpty" when i + 1 < args.Length: conpty = args[++i]; break;
    }
}
if (string.IsNullOrWhiteSpace(pipe)) return 1;
string appId = Environment.GetEnvironmentVariable("AGWINTERM_APP_ID") ?? "agwinterm";
string logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appId, "logs");
using var startupJournal = new StartupJournal(logDirectory, "host");
var activity = new ActivityJournal(logDirectory);
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    activity.Write("Host", ActivityLevel.Error, "Host stopped unexpectedly", (e.ExceptionObject as Exception)?.Message);
string configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    appId, "agwinterm.conf");
TerminalConfig config;
try { config = TerminalConfig.Load(configPath); }
catch (IOException) { config = new(); }
catch (UnauthorizedAccessException) { config = new(); }
if (!GhosttyTerminalCore.TryLoad(AppContext.BaseDirectory, out string? error))
    throw new InvalidOperationException("Ghostty terminal backend is unavailable: " + error);
TerminalSession.CoreFactory = (cols, rows) => new GhosttyTerminalCore(cols, rows, config.CursorStyle, config.CursorBlink);
ConPtyApi.Select((conpty ?? config.Conpty) == "bundled", AppContext.BaseDirectory);
using var server = new PtyHostServer(pipe);
startupJournal.Mark("host.listening");
activity.Write("Host", ActivityLevel.Info, "Session host started");
server.Completion.Wait();
return 0;

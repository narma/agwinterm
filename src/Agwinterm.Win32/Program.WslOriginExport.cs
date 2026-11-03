using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Win32;

internal partial class Program
{
    private object ExportOriginTree(JsonElement args)
    {
        var request = WslOriginExportRequest.Parse(args, null);
        if (request.Scope.OriginId != _appId)
            throw new InvalidOperationException("Export endpoint belongs to another application origin");
        IReadOnlyList<WslOriginSession> Capture() => InvokeOnUiQueued(() =>
        {
            lock (_windowIndex)
                if (!_windowIndex.Any(w => w.Id == Id && w.IsOpen))
                    throw new InvalidOperationException("Origin window is no longer open");
            return (IReadOnlyList<WslOriginSession>)AllSessions().Where(s => s.PageParentId is null)
                .Select(s =>
                {
                    var panes = s.AllPanes.Select((p, index) => new WslOriginPane(p.Id,
                        index == 0 ? "left" : "right", RestoreState.LoadLiveBinding(p.LiveBinding, out _),
                        null, !p.S.HasExited && p.Start is not { IsCompleted: false }, p.LiveClosing || s.Closing)).ToArray();
                    return new WslOriginSession(s.Id, s.SidebarName, Id, WinName, s.Ws.Id, s.Ws.Name,
                        panes.Length == 0 ? "" : s.AllPanes.First().StartCwd ?? "",
                        panes.Length == 2 ? s.Axis : null, panes);
                }).ToArray();
        });
        string envelope = WslOriginDiscovery.DiscoverAsync(request, Capture,
            _liveOperations.ObserveOriginAsync).GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(envelope);
        // ControlServer adds the outer ok/result envelope exactly once.
        var result = document.RootElement.GetProperty("result").Deserialize<Dictionary<string, JsonElement>>()!;
        // Advertise readiness alongside the already verified tree, without a second daemon probe.
        result["presentation"] = JsonSerializer.SerializeToElement(1);
        return JsonSerializer.SerializeToElement(result);
    }
}

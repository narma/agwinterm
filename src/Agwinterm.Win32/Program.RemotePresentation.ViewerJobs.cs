using System.Text.Json;
using System.Globalization;
using Agwinterm.Pty;
using Agwinterm.Core;

namespace Agwinterm.Win32;

internal partial class Program
{
    private sealed class ReplicaJob(Ses session, Pane? pane, PresentationReplica replica, AgtermPresentationSender sender, string id)
    {
        public readonly Ses Session = session;
        public readonly Pane? Pane = pane;
        public readonly PresentationReplica Replica = replica;
        public readonly AgtermPresentationSender Sender = sender;
        public readonly string Id = id;
        public bool Orphaned;
    }
    private readonly Dictionary<Pane, ReplicaJob> _replicaJobs = new();

    private bool ApplyReplicaJob(Ses session, PresentationReplica replica, AgtermPresentationFrame frame, Dictionary<Guid, string> mapping)
    {
        if (frame.Kind is not ("overlay.request" or "overlay.close" or "overlay.resize")) return false;
        var body = frame.Body;
        string job = body.GetProperty("job").GetString()!;
        if (job.Length is < 1 or > 256 || job.Any(char.IsControl)) throw new IOException("Invalid overlay job ID");
        if (replica.Sender is not { Granted: true } sender) return true;
        var existing = _replicaJobs.FirstOrDefault(p => ReferenceEquals(p.Value.Replica, replica)
            && ReferenceEquals(p.Value.Sender, sender) && p.Value.Id == job && !p.Value.Orphaned);
        if (frame.Kind == "overlay.close")
        {
            if (existing.Value is { } found) { if (found.Pane is null) CloseOverlayOf(session); else ClosePaneOverlay(session, found.Pane); }
            return true;
        }
        int percent = body.TryGetProperty("sizePercent", out var size) && size.ValueKind != JsonValueKind.Null ? size.GetInt32() : 0;
        if (percent is < 0 or > 100 || body.TryGetProperty("width", out var width) && width.ValueKind != JsonValueKind.Null
            || body.TryGetProperty("height", out var height) && height.ValueKind != JsonValueKind.Null)
        { sender.Send("overlay.rejected", JsonSerializer.SerializeToElement(new { job })); return true; }
        if (frame.Kind == "overlay.resize")
        {
            if (existing.Value is { Pane: null })
            {
                session.Overlay.SizePercent = percent;
                var (_, _, w, h) = OverlayRect(session.Overlay);
                if (!Win32.IsIconic(_hwnd)) RegridTerm(existing.Key, w, h);
                RequestRedraw();
            }
            return true;
        }
        if (existing.Value is not null) return true;
        try
        {
            if (_sessionBackend.Name != "server-rust") throw new IOException("Remote jobs require Rust PTY hosting");
            Pane? pane = null;
            if (body.TryGetProperty("pane", out var address) && address.ValueKind != JsonValueKind.Null)
            {
                if (!Guid.TryParse(address.GetString(), out var origin) || !mapping.TryGetValue(origin, out var local)) throw new IOException("Unmapped job pane");
                pane = session.Panes.SingleOrDefault(p => p.Id == local) ?? throw new IOException("Job pane is hidden or gone");
                if (percent != 0) throw new IOException("Pane jobs are full-pane");
            }
            var slot = pane?.Overlay ?? session.Overlay;
            if (slot.Term is not null || slot.OriginJob?.Occupied == true || _webPages.ContainsKey((object?)pane ?? session)
                || pane is null && session.Hud is not null) throw new IOException("Viewer overlay slot occupied");
            string? color = body.TryGetProperty("backgroundColor", out var bg) && bg.ValueKind != JsonValueKind.Null ? bg.GetString() : null;
            uint rgb = 0;
            if (color is not null && (color.Length != 7 || color[0] != '#' || !uint.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb)))
                throw new IOException("Invalid overlay color");
            bool wait = body.GetProperty("wait").GetBoolean(), follow = body.GetProperty("follow").GetBoolean();
            var launch = AgtermRemoteOperations.OverlayJobCommand(replica.Bindings[0], job);
            string reply = OverlayOpen(session, pane, "", percent, wait, directLaunch: launch);
            if (reply.StartsWith(ISessionHost.RefusePrefix, StringComparison.Ordinal) || slot.Term is null) throw new IOException(reply);
            _replicaJobs.Add(slot.Term, new(session, pane, replica, sender, job));
            if (color is not null) slot.Term.DefaultBackground = new Color((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            if (follow) { if (pane is not null) session.Active = session.Panes.IndexOf(pane); SetActive(session); SyncSession(); }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        { sender.Send("overlay.rejected", JsonSerializer.SerializeToElement(new { job })); session.PresentationError = ex.Message; }
        return true;
    }

    private void CloseReplicaJob(Pane term)
    {
        if (!_replicaJobs.Remove(term, out var job) || job.Orphaned) return;
        try { job.Sender.Send("overlay.closed", JsonSerializer.SerializeToElement(new { job = job.Id })); } catch (IOException) { }
    }

    private void OrphanReplicaJobs(PresentationReplica replica)
    {
        foreach (var (term, job) in _replicaJobs.Where(p => ReferenceEquals(p.Value.Replica, replica)).ToArray())
        {
            job.Orphaned = true;
            var slot = job.Pane?.Overlay ?? job.Session.Overlay;
            if (!ReferenceEquals(slot.Term, term)) { _replicaJobs.Remove(term); continue; }
            if (!slot.Exited && !term.S.HasExited) continue;
            if (job.Pane is null) CloseOverlayOf(job.Session); else ClosePaneOverlay(job.Session, job.Pane);
        }
    }
}

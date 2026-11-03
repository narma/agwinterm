using System.Text.Json;
using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Win32;

internal partial class Program
{
    private static readonly string[] PageKinds = ["page.request", "page.rejected", "page.close", "page.closed", "page.command", "page.state"];
    private sealed record OriginPagePresenter(AgtermOriginPresentationScope Scope, OriginPresentation Entry,
        AgtermPresentationBroker.Subscription Subscriber, long Owner);
    private sealed class OriginPage(AgtermPageGrant grant, OriginPagePresenter presenter, AgtermPageFiles? files)
    {
        public readonly AgtermPageGrant Grant = grant;
        public readonly OriginPagePresenter Presenter = presenter;
        public readonly AgtermPageFiles? Files = files;
        public long Sequence, AppliedSequence;
    }
    private readonly Dictionary<string, WebPage> _originPages = new(StringComparer.Ordinal);

    private OriginPagePresenter? FindOriginPagePresenter(Ses owner)
    {
        foreach (var (scope, entry) in _originPresentations)
            if (ReferenceEquals(entry.Session, owner) && OriginPresentationCurrent(scope, entry)
                && _presentationBroker.Presenter(scope) is { } presenter && PageKinds.All(presenter.Subscriber.Kinds.Contains))
                return new(scope, entry, presenter.Subscriber, presenter.Owner);
        return null;
    }

    private bool CurrentOriginPage(WebPage page) => page.Origin is { } remote && remote.Grant.IsAlive
        && OwnsWebPage(page.Owner, page) && OriginPresentationCurrent(remote.Presenter.Scope, remote.Presenter.Entry)
        && _presentationBroker.Authorizes(remote.Presenter.Scope, remote.Presenter.Subscriber, remote.Presenter.Owner);

    private void StartOriginPage(WebPage page, OriginPagePresenter presenter)
    {
        if (_originPages.Count >= 256 || !OriginPresentationCurrent(presenter.Scope, presenter.Entry)
            || !_presentationBroker.Authorizes(presenter.Scope, presenter.Subscriber, presenter.Owner))
            throw new IOException("Page presenter or slot unavailable");
        var grant = new AgtermPageGrant(presenter.Scope, presenter.Subscriber.Generation, presenter.Owner,
            page.Resources is not null, Environment.TickCount64);
        var files = page.Resources is null ? null : new AgtermPageFiles(grant.Page, page.Resources);
        page.Origin = new(grant, presenter, files);
        _originPages.Add(grant.Token, page);
        grant.Ended += () => Post(() => { if (OwnsWebPage(page.Owner, page)) CloseWebPage(page.Owner, page.Pane); });
        var request = page.Request;
        object source = files is null ? new { url = new { _0 = request.Url } }
            : new { file = new { path = files.Entry, grantRoot = request.Cwd is null ? null : files.Root } };
        var payload = JsonSerializer.SerializeToElement(new
        {
            page = grant.Page, token = grant.Token, source, identity = page.Resources?.ExternalPath ?? request.Url,
            chromeless = request.Chromeless, javascript = request.JavaScript, navigation = request.Navigation,
            browse = request.Browse, persistent = request.Persistent, backgroundColor = request.Background,
            pane = page.Pane is null ? (Guid?)null : presenter.Entry.Mapping[page.Pane.Id],
            sizePercent = page.Pane is null ? request.Percent : (int?)null, width = request.Width, height = request.Height, follow = request.Follow
        });
        if (!_presentationBroker.SendPresenter(presenter.Scope, presenter.Subscriber, presenter.Owner, "page.request", payload))
            throw new IOException("Page presenter unavailable");
        _ = Task.Delay(30000, _uiGone.Token).ContinueWith(_ => Post(() => grant.Expire(Environment.TickCount64)), TaskScheduler.Default);
    }

    AgtermOriginPageLease IAgtermPresentationOriginHost.OpenOverlayPage(string session, string token, JsonElement args, string account)
    {
        _ = ExportOriginTree(args);
        var scope = new AgtermOriginPresentationScope(account, Id, session, WslOriginExportRequest.Parse(args, null).Scope);
        return InvokeOnUiQueued(() =>
        {
            if (!_originPages.TryGetValue(token, out var page) || page.Origin is not { Files: { } files } remote
                || remote.Grant.Scope != scope || !CurrentOriginPage(page)) throw new IOException("Page grant unavailable");
            _presentationBroker.ClaimPage(remote.Grant, remote.Presenter.Subscriber, Environment.TickCount64);
            return new AgtermOriginPageLease(remote.Grant, async (path, ct) =>
            {
                InvokeOnUiQueued(() => { if (!CurrentOriginPage(page)) throw new IOException("Page retired"); return true; });
                return await files.Open(path, ct).ConfigureAwait(false);
            }, (json, ct) => DispatchOriginPageBridge(page, json, ct));
        });
    }

    private Task<string> DispatchOriginPageBridge(WebPage page, string json, CancellationToken ct)
    {
        var request = AgtermPageBridge.Parse(json, page.Request.Bridge == "review");
        long generation = InvokeOnUiQueued(() =>
        { if (!CurrentOriginPage(page)) throw new IOException("Page retired"); return page.DocumentGeneration; });
        return Task.Run(() =>
        {
            Action guard = () =>
            {
                ct.ThrowIfCancellationRequested();
                if (!CurrentOriginPage(page) || page.DocumentGeneration != generation) throw new IOException("Page document retired");
            };
            string? envelope = InvokeOnUiQueued(() =>
            {
                guard();
                if (request.Command.StartsWith("session.overlay.", StringComparison.Ordinal))
                { DispatchOwnOriginPage(page, request); return null; }
                return BuildWebCommand(page, new WebBridgeRequest("remote", request.Command, null, null, request.Args));
            });
            if (envelope is null) return JsonSerializer.Serialize(new { ok = true, result = new { pageID = page.Id } });
            WebCommandGuard.Value = guard;
            try
            {
                using var dispatcher = new ControlServer(this, this, sessionCommandExecutor: fn => InvokeOnUiQueued(fn));
                return dispatcher.Dispatch(envelope);
            }
            finally { WebCommandGuard.Value = null; }
        }, ct);
    }

    private void DispatchOwnOriginPage(WebPage page, AgtermPageBridge request)
    {
        // Session-wide CLI hide/show affects every overlay. A page bridge must address only its own slot.
        string action = request.Command["session.overlay.".Length..];
        var args = request.Args;
        if (action is "submit" or "close")
        {
            if (action == "submit" && !CompleteWebOutcome(page, args.GetProperty("value").GetString()))
                throw new IOException("Page already completed");
            Post(() => { if (OwnsWebPage(page.Owner, page)) CloseWebPage(page.Owner, page.Pane); });
            return;
        }
        if (action is "hide" or "show" or "toggle")
        {
            page.Hidden = action == "hide" || action == "toggle" && !page.Hidden;
            action = page.Hidden ? "hide" : "show";
        }
        else if (action == "resize")
        {
            if (page.Pane is not null) throw new IOException("Pane page is full-pane");
            int? percent = PageSize(args, "size-percent", 100), width = PageSize(args, "width", 16384), height = PageSize(args, "height", 16384);
            page.Geometry = page.Geometry.Resize(percent, width, height, percent is null && width is null && height is null);
        }
        else if (action == "reload") action = !args.TryGetProperty("current", out var current) || current.GetBoolean() ? "reloadCurrent" : "reloadOriginal";
        else if (action == "navigate") action = args.GetProperty("direction").GetString()!;
        SendOriginPageCommand(page, action);
    }

    private void SendOriginPageCommand(WebPage page, string action)
    {
        if (!CurrentOriginPage(page) || page.Origin is not { } remote) throw new ArgumentException("Page presenter unavailable");
        if (action is not ("hide" or "show" or "resize" or "reloadCurrent" or "reloadOriginal" or "back" or "forward" or "browser"))
            throw new ArgumentException("Unsupported remote page operation");
        if (action == "browser" && page.Resources is not null) throw new ArgumentException("Remote file cannot be opened from viewer disk");
        if (action is "reloadCurrent" or "reloadOriginal" or "back" or "forward") page.BeginDocument();
        var geometry = page.Geometry;
        var payload = JsonSerializer.SerializeToElement(new { page = remote.Grant.Page, seq = ++remote.Sequence, action,
            sizePercent = action == "resize" ? geometry.Percent : (int?)null,
            width = action == "resize" ? geometry.WidthDip : null, height = action == "resize" ? geometry.HeightDip : null });
        if (!_presentationBroker.SendPresenter(remote.Presenter.Scope, remote.Presenter.Subscriber, remote.Presenter.Owner, "page.command", payload))
            throw new ArgumentException("Page presenter unavailable");
    }

    private void EndOriginPage(WebPage page)
    {
        if (page.Origin is not { } remote) return;
        _originPages.Remove(remote.Grant.Token);
        _presentationBroker.SendPresenter(remote.Presenter.Scope, remote.Presenter.Subscriber, remote.Presenter.Owner,
            "page.close", JsonSerializer.SerializeToElement(new { page = remote.Grant.Page }));
        remote.Grant.Close();
    }

    private void CloseOriginPages(AgtermOriginPresentationScope scope, AgtermPresentationBroker.Subscription subscriber)
    {
        foreach (var page in _originPages.Values.Where(p => p.Origin!.Presenter.Scope == scope
            && ReferenceEquals(p.Origin.Presenter.Subscriber, subscriber)).ToArray()) CloseWebPage(page.Owner, page.Pane);
    }

    private void ReceiveOriginPage(AgtermOriginPresentationScope scope, AgtermPresentationBroker.Subscription subscriber, AgtermPresentationFrame frame)
    {
        string id = frame.Body.GetProperty("page").GetString()!;
        var page = _originPages.Values.SingleOrDefault(p => p.Origin!.Grant.Page.Equals(id, StringComparison.OrdinalIgnoreCase)
            && p.Origin.Presenter.Scope == scope && ReferenceEquals(p.Origin.Presenter.Subscriber, subscriber));
        if (page?.Origin is not { } remote || !CurrentOriginPage(page)) return;
        if (frame.Kind is "page.closed" or "page.rejected") { CloseWebPage(page.Owner, page.Pane); return; }
        long applied = frame.Body.GetProperty("appliedSeq").GetInt64();
        if (applied < remote.AppliedSequence || applied != remote.Sequence) return;
        remote.AppliedSequence = applied;
        string? Text(string key, int limit)
        {
            if (!frame.Body.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
            var text = value.GetString();
            if (text?.Length > limit) throw new IOException("Page state exceeds limit");
            return text;
        }
        page.Title = Text("title", 1024); page.CurrentUrl = Text("address", 8192); page.Error = Text("error", 1024);
        string state = Text("load", 32) ?? "loading";
        if (state is not ("loading" or "loaded" or "failed")) throw new IOException("Unknown page load state");
        if (state == "loaded" && page.State != "loaded" && page.Pane is null) ClearHud(page.Owner);
        page.State = state;
        page.CanGoBack = frame.Body.GetProperty("canGoBack").GetBoolean(); page.CanGoForward = frame.Body.GetProperty("canGoForward").GetBoolean();
        if (applied == remote.Sequence) page.Hidden = frame.Body.GetProperty("hidden").GetBoolean();
    }
}

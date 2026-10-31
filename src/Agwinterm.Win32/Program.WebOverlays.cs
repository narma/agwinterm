using System.Drawing;
using Agwinterm.Pty;
using Agwinterm.Core;
using Microsoft.Web.WebView2.Core;

namespace Agwinterm.Win32;

internal partial class Program
{
    private sealed class WebPage(WebOverlayRequest request, WebViewHost host, Ses owner, Pane? pane) : WebPageState(new(request.Percent, request.Width, request.Height))
    {
        public readonly Ses Owner = owner;
        public readonly Pane? Pane = pane;
        public object Slot => (object?)Pane ?? Owner;
        public readonly WebOverlayRequest Request = request;
        public WebViewHost Host = host;
        public bool RecreateHost;
        public readonly CancellationTokenSource Cancellation = new();
        public Rectangle Bounds;
        public bool ResumeFocus;
        public string? BridgeToken;
        public LocalWebSource? Resources;
        public string? ReloadUrl;
        public IntPtr[] NavigationButtons = [];
        public Theme? AppliedTheme;
        public IntPtr ErrorLabel;
        public Rectangle? NativeBounds;
        public bool? NavigationVisible;
        public bool? ErrorVisible;
        public string? PresentedError;
        public string? ExternalPromptId;
        public bool ExternalPromptQueued;
        public bool ExternalPromptsSilenced;
        public uint ExternalDeniedInputTick;
        public IntPtr OriginLabel;
        public string? CommittedUrl;
        public string? PresentedOrigin;
        public string? Title;
        public bool CanGoBack;
        public bool CanGoForward;
        public string? ThemeScriptId;
        public readonly TaskCompletionSource<WebPageOutcome> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[]? ImageDocument;
        public WebImageInfo? ImageInfo;
        public bool CloseRequested;
    }
    private readonly WebPageSlots<object, WebPage> _webPages = new();
    private static readonly WebPageOutcomes WebOutcomes = new();

    private (Ses owner, Pane? pane) ResolveWebOwner(string? target, string? selector, bool opening)
    {
        var hidden = target is null ? null : FindHiddenPaneByExactId(target);
        var owner = FindSessionByExactId(target ?? "") ?? FindSesForTarget(target) ?? hidden?.ses
            ?? throw new ArgumentException("web overlay owner not found");
        Pane? pane = null;
        if (selector is not null)
        {
            if (!OverlayPanes.TryParse(selector, out int index, out var error)) throw new ArgumentException(error);
            var located = LocatePaneSlot(target, index);
            if (located.early is not null) throw new ArgumentException(located.early);
            pane = located.p;
        }
        else if (target is not (null or "active") && target != owner.Id)
            pane = owner.AllPanes.FirstOrDefault(p => p.Id == target)
                ?? throw new ArgumentException("web overlays require an exact session or pane target");
        if (opening && pane is not null && !owner.Panes.Contains(pane)) throw new ArgumentException("cannot open a web overlay on a hidden pane");
        return (owner, pane);
    }

    public object WebOverlayOperation(string? target, WebOverlayCommand request)
    {
        if (request.PageId is { } id)
        {
            var result = WebOutcomes.Read(id);
            return result.Value is null
                ? (object)new { pageOutcome = new { pageID = id, outcome = result.Outcome } }
                : new { pageOutcome = new { pageID = id, outcome = result.Outcome, value = result.Value } };
        }
        return InvokeOnUiQueued<object>(() =>
        {
            var (owner, pane) = ResolveWebOwner(target, request.Pane, opening: false);
            if (!_webPages.TryGetValue((object?)pane ?? owner, out var page)) throw new ArgumentException("no web overlay on this slot");
            if (request.Action == "reload") { ReloadWebPage(owner, page, request.Current); return new { pageID = page.Id }; }
            var browser = page.Host.Page;
            if (request.Direction == "back") { if (!browser.CanGoBack) throw new ArgumentException("no previous page"); browser.GoBack(); }
            else if (request.Direction == "forward") { if (!browser.CanGoForward) throw new ArgumentException("no next page"); browser.GoForward(); }
            else if (request.Direction == "finder") RevealWebFile(page);
            else if (request.Direction == "browser")
            {
                OpenWebInBrowser(page);
            }
            return new { pageID = page.Id };
        });
    }

    public object OpenWebOverlay(string? target, WebOverlayRequest request)
    {
        // Probe before touching any slot; browser assembly/loader activation is web-only.
        _ = WebViewHost.RuntimeVersion();
        if (request.Persistent) EnsurePersistentWebStore();
        var context = InvokeOnUiQueued(() =>
        {
            if (request.Persistent && _webStorageClearing) throw new ArgumentException("browser storage is being cleared");
            var (owner, pane) = ResolveWebOwner(target, request.Pane, opening: true);
            if (!request.Companion && (owner.PageParentId is not null || (pane?.Overlay ?? owner.Overlay).Term is not null || _webPages.ContainsKey((object?)pane ?? owner))) throw new ArgumentException("a program or web overlay occupies this slot");
            if (pane is not null && (request.Percent != 100 || request.Width is not null || request.Height is not null))
                throw new ArgumentException("pane overlays are always full-pane");
            return (owner, pane, cwd: CwdOf(pane ?? owner.ActivePane), profile: ResolveProfile(owner.ProfileName));
        });
        LocalWebSource? resources = null;
        byte[]? imageDocument = null;
        WebImageInfo? imageInfo = null;
        if ((request.Html ?? request.Image) is { } file)
        {
            resources = LocalWebSource.Open(file, context.cwd, context.profile, request.Cwd, request.Image is null ? int.MaxValue : WebImageInfo.MaxBytes);
            if (request.Image is not null)
            {
                byte[] bytes = resources.Read("/" + resources.EntryUrlPath, WebImageInfo.MaxBytes);
                imageInfo = WebImageInfo.Parse(bytes);
                imageDocument = System.Text.Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><style>html,body{margin:0;height:100%;overflow:hidden}img{width:100%;height:100%;object-fit:contain}</style><img alt=\"Image overlay\" src=\"data:" + imageInfo.Mime + ";base64," + Convert.ToBase64String(bytes) + "\">");
            }
        }
        var opened = InvokeOnUiQueued(() =>
        {
            if (request.Persistent && _webStorageClearing) throw new ArgumentException("browser storage is being cleared");
            var pageOwner = request.Companion ? CreatePageCompanion(context.owner, request) : context.owner;
            var page = new WebPage(request, NewWebHost(), pageOwner, context.pane);
            page.Resources = resources;
            page.ImageDocument = imageDocument; page.ImageInfo = imageInfo;
            if (!_webPages.TryReserve(page.Slot, page, AllSessions().Contains(pageOwner) && (context.pane is null || pageOwner.Panes.Contains(context.pane)), (context.pane?.Overlay ?? pageOwner.Overlay).Term is not null))
            {
                page.Host.Dispose();
                throw new ArgumentException("web overlay owner or slot changed while validating the source");
            }
            WebOutcomes.Register(page.Id);
            StartWebPage(pageOwner, page);
            return page;
        });
        if (!request.Block) return request.Companion ? (object)new { id = opened.Owner.Id, parent = opened.Owner.PageParentId, pageID = opened.Id } : new { pageID = opened.Id };
        var outcome = opened.Completion.Task.WaitAsync(_uiGone.Token).GetAwaiter().GetResult();
        return outcome.Value is null
            ? (object)new { pageOutcome = new { pageID = outcome.PageId, outcome = outcome.Outcome } }
            : new { pageOutcome = new { pageID = outcome.PageId, outcome = outcome.Outcome, value = outcome.Value } };
    }

    private void StartWebPage(Ses owner, WebPage page)
    {
        CreateWebNavigation(page);
        var host = page.Host;
        var request = page.Request;
        var resources = page.Resources;
        bool IsCurrent() => OwnsWebPage(owner, page) && ReferenceEquals(page.Host, host);
        host.Accelerator = e => { if (IsCurrent()) WebAccelerator(owner, page, e); };
        host.Focused = () =>
        {
            if (!IsCurrent() || owner.Closing || page.Pane is null) return;
            int index = owner.Panes.IndexOf(page.Pane);
            if (index >= 0 && ReferenceEquals(owner, _active)) { owner.Active = index; SyncSession(); RequestRedraw(); }
        };
        host.ZoomChanged = zoom => { if (IsCurrent()) { page.Zoom = zoom; if (Math.Abs(BrowserPreferences.Zoom - zoom) > .001) SetWebZoom(zoom); } };
        host.MoveFocusRequested = e =>
        {
            if (!IsCurrent() || page.NavigationButtons.Length == 0) return;
            e.Handled = true;
            Win32.SetFocus(e.Reason == CoreWebView2MoveFocusReason.Previous ? page.NavigationButtons[^1] : page.NavigationButtons[0]);
        };
        // Keep the HUD until successful controller initialization commits presentation.
        _ = host.RunAsync(async () =>
        {
            try
            {
                await host.CreateAsync(_hwnd, Path.Combine(AppDir, request.Persistent ? "webview2-persistent" : "webview2"), page.Id, page.Cancellation.Token, request.Persistent);
                if (!IsCurrent()) { host.Dispose(); return; }
                var browser = host.Page;
                page.Zoom = BrowserPreferences.Zoom;
                host.SetZoom(page.Zoom);
                browser.DocumentTitleChanged += (_, _) => { if (IsCurrent()) { page.Title = browser.DocumentTitle; RequestRedraw(); } };
                browser.HistoryChanged += (_, _) => { if (IsCurrent()) { page.CanGoBack = browser.CanGoBack; page.CanGoForward = browser.CanGoForward; RequestRedraw(); } };
                CreateWebNavigation(page);
                browser.DOMContentLoaded += (_, _) => { if (IsCurrent()) ApplyWebTheme(page, documentLoaded: true); };
                // Trusted local declarative controls need event callbacks even when document
                // scripts are disabled. Enforce that restriction with the response CSP below;
                // WebView2's global switch also disables callbacks in injected host scripts.
                browser.Settings.IsScriptEnabled = request.JavaScript || resources is not null && request.Image is null;
                string source = OriginalWebUrl(page);
                var origin = new Uri(source).GetLeftPart(UriPartial.Authority);
                ulong navigationId = 0;
                bool userNavigation = false;
                string? loadedUrl = null;
                var blockedNavigations = new Dictionary<ulong, string>();
                browser.SourceChanged += (_, _) => { if (IsCurrent()) page.CurrentUrl = browser.Source; };
                browser.ContentLoading += (_, e) => { if (IsCurrent() && !e.IsErrorPage) { page.CommittedUrl = browser.Source; RequestRedraw(); } };
                browser.NavigationStarting += (_, e) =>
                {
                    if (!IsCurrent()) { e.Cancel = true; return; }
                    if (!e.IsRedirected || navigationId != e.NavigationId) userNavigation = e.IsUserInitiated;
                    navigationId = e.NavigationId;
                    if (!WebNavigationPolicy.Allows(source, e.Uri, resources is not null, request.Bridge is not null, request.Browse))
                    {
                        blockedNavigations[e.NavigationId] =
                            $"Navigation blocked: {e.Uri} is outside this page's allowed location. For web redirects to other sites, reopen with --url and --browse.";
                        if (userNavigation) OfferWebExternal(owner, page, e.Uri);
                        e.Cancel = true; return;
                    }
                    page.BridgeToken = null;
                    page.BeginDocument();
                };
                browser.NewWindowRequested += (_, e) =>
                {
                    e.Handled = true;
                    if (!IsCurrent()) return;
                    if (e.IsUserInitiated) OfferWebExternal(owner, page, e.Uri);
                };
                browser.WindowCloseRequested += (_, _) =>
                {
                    if (!IsCurrent()) return;
                    page.CloseRequested = true;
                    PostVerb(() => { if (IsCurrent()) CloseWebPage(owner, page.Pane); });
                };
                browser.FrameNavigationStarting += (_, e) =>
                {
                    e.Cancel = !WebNavigationPolicy.AllowsFrame(source, e.Uri, resources is not null, request.Bridge is not null, request.Chromeless);
                };
                browser.NavigationCompleted += (_, e) =>
                {
                    if (!IsCurrent() || page.CloseRequested) return;
                    bool blocked = blockedNavigations.Remove(e.NavigationId, out var reason);
                    // A canceled/superseded navigation must not overwrite a newer document.
                    if (e.NavigationId != navigationId) return;
                    if (!blocked && !e.IsSuccess && e.WebErrorStatus is CoreWebView2WebErrorStatus.OperationCanceled or CoreWebView2WebErrorStatus.ConnectionAborted)
                    {
                        // window.close and superseding navigation can report cancellation
                        // before the close/new-navigation event reaches the STA queue.
                        _ = host.RunAsync(async () =>
                        {
                            await Task.Delay(100);
                            if (!IsCurrent() || page.CloseRequested || navigationId != e.NavigationId) return;
                            page.State = loadedUrl is null ? "failed" : "loaded";
                            page.Error = loadedUrl is null ? "Navigation was canceled" : null;
                            if (loadedUrl is null && page.Request.Block) page.Completion.TrySetException(new IOException(page.Error));
                            RequestRedraw();
                        });
                        return;
                    }
                    if (blocked)
                    {
                        page.State = loadedUrl is null ? "failed" : "loaded";
                        page.Error = loadedUrl is null ? reason : null;
                        if (loadedUrl is null && page.Request.Block) page.Completion.TrySetException(new IOException(reason));
                        page.CurrentUrl = loadedUrl ?? browser.Source;
                        RequestRedraw();
                        return;
                    }
                    if (e.IsSuccess) loadedUrl = browser.Source;
                    if (e.IsSuccess && page.ImageInfo is not null)
                    {
                        _ = host.RunAsync(async () =>
                        {
                            string decoded = await browser.ExecuteScriptAsync("Boolean(document.images[0]?.complete && document.images[0]?.naturalWidth > 0)");
                            if (!IsCurrent() || navigationId != e.NavigationId) return;
                            page.State = decoded == "true" ? "loaded" : "failed";
                            page.Error = decoded == "true" ? null : "Unreadable image";
                            RequestRedraw();
                        });
                    }
                    page.State = e.IsSuccess ? page.ImageInfo is null ? "loaded" : "loading" : "failed";
                    page.Error = e.IsSuccess ? null : e.WebErrorStatus.ToString();
                    if (!e.IsSuccess && page.Request.Block) page.Completion.TrySetException(new IOException(page.Error));
                    RequestRedraw();
                };
                browser.ProcessFailed += (_, e) =>
                {
                    if (!IsCurrent()) return;
                    page.State = "failed"; page.Error = e.ProcessFailedKind.ToString();
                    page.RecreateHost = e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited;
                    page.BridgeToken = null;
                    if (ReferenceEquals(owner, _active)) Win32.SetFocus(_hwnd);
                    if (page.Request.Block) page.Completion.TrySetException(new IOException(page.Error));
                    RequestRedraw();
                };
                if (resources is not null)
                {
                    browser.AddWebResourceRequestedFilter(origin + "/*", CoreWebView2WebResourceContext.All,
                        CoreWebView2WebResourceRequestSourceKinds.All);
                    browser.WebResourceRequested += (_, e) =>
                    {
                        var deferral = e.GetDeferral();
                        string path = new Uri(e.Request.Uri).AbsolutePath;
                        _ = host.RunAsync(async () =>
                        {
                            try
                            {
                                byte[] bytes = page.ImageDocument ?? await Task.Run(() => resources.Read(path));
                                if (!IsCurrent()) return;
                                e.Response = browser.Environment.CreateWebResourceResponse(new MemoryStream(bytes, false), 200, "OK",
                                    "Content-Type: " + (page.ImageDocument is null ? LocalWebSource.ContentType(path) : "text/html; charset=utf-8") + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff" +
                                    (!request.JavaScript ? "\r\nContent-Security-Policy: script-src 'none'; object-src 'none'" :
                                     request.Bridge is not null ? "\r\nContent-Security-Policy: frame-src 'none'; object-src 'none'" : ""));
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                            {
                                if (IsCurrent())
                                    e.Response = browser.Environment.CreateWebResourceResponse(Stream.Null, 403, "Resource unavailable or outside document grant", "Cache-Control: no-store");
                            }
                            finally { try { deferral.Complete(); } catch (InvalidOperationException) { } }
                        });
                    };
                }
                if (request.Bridge == "review") await BindReview(owner, page, source);
                else if (resources is not null && request.Image is null) await BindLocalCommands(owner, page, source);
                if (!IsCurrent()) return;
                if (!_webPages.CommitPresentation(page.Slot, page, () => { if (page.Pane is null) ClearHud(owner); })) return;
                browser.Navigate(page.ReloadUrl ?? source);
                page.ReloadUrl = null;
                if (request.Follow) { if (page.Pane is not null) owner.Active = owner.Panes.IndexOf(page.Pane); SetActive(owner); SyncSession(); }
                UpdateWebPresentation();
                RequestRedraw();
            }
            catch (OperationCanceledException) { host.Dispose(); }
            catch (Exception ex)
            {
                host.Dispose();
                if (IsCurrent()) { page.State = "failed"; page.Error = ex.Message; page.RecreateHost = true; if (page.Request.Block) page.Completion.TrySetException(ex); RequestRedraw(); }
            }
        });
    }

    private void ReloadWebPage(Ses owner, WebPage page, bool current = true)
    {
        CancelWebExternal(page);
        if (!page.RecreateHost)
        {
            var browser = page.Host.Page;
            page.BridgeToken = null;
            page.BeginDocument();
            if (current) browser.Reload();
            else browser.Navigate(OriginalWebUrl(page));
            RequestRedraw();
            return;
        }
        page.Host.Dispose();
        page.ReloadUrl = current ? page.CurrentUrl : null;
        page.Host = NewWebHost();
        page.RecreateHost = false;
        page.BridgeToken = null;
        page.BeginDocument();
        StartWebPage(owner, page);
    }

    private static string OriginalWebUrl(WebPage page) => page.Request.Url ??
        $"https://page-{page.Id}.invalid/{page.Resources!.EntryUrlPath}";

    private bool OwnsWebPage(Ses owner, WebPage page) =>
        !_uiGone.IsCancellationRequested && _webPages.GetValueOrDefault(page.Slot) == page && !page.Cancellation.IsCancellationRequested;

    private void CloseWebPage(Ses owner, Pane? pane = null)
    {
        if (!_webPages.Remove((object?)pane ?? owner, out var page)) return;
        CancelWebExternal(page);
        CompleteWebOutcome(page, null);
        page.Cancellation.Cancel();
        foreach (var button in page.NavigationButtons) Win32.DestroyWindow(button);
        if (page.ErrorLabel != IntPtr.Zero) Win32.DestroyWindow(page.ErrorLabel);
        if (page.OriginLabel != IntPtr.Zero) Win32.DestroyWindow(page.OriginLabel);
        page.Host.Dispose();
        if (ReferenceEquals(owner, _active)) Win32.SetFocus(_hwnd);
        if (owner.PageParentId is not null && !owner.Closing && OwnsSession(owner)) CloseSessionInternal(owner, undoable: false);
        RequestRedraw();
    }

    private static bool CompleteWebOutcome(WebPage page, string? value)
    {
        if (!WebOutcomes.Complete(page.Id, value)) return false;
        page.Completion.TrySetResult(new(page.Id, value is null ? "dismissed" : "submitted", value));
        return true;
    }

    private void UpdateWebPresentation()
    {
        foreach (var (_, page) in _webPages)
        {
            var owner = page.Owner;
            RefreshWebExternalInput(page);
            if (!ReferenceEquals(owner, _active) || page.Hidden || _cover is not null) CancelWebExternal(page);
            var (x, y, width, height) = ContentArea();
            bool paneVisible = true;
            if (page.Pane is { } pane)
            {
                var layout = PaneLayout(owner).FirstOrDefault(item => ReferenceEquals(item.pane, pane));
                paneVisible = layout.pane is not null;
                (x, y, width, height) = (layout.x, layout.y, layout.w, layout.h);
                if (_webPages.TryGetValue(owner, out var cover) && !cover.Hidden) paneVisible = false;
            }
            var box = page.Geometry
                .Fit(x * Scale, y * Scale, width * Scale, height * Scale, _dpi);
            if (page.ImageInfo is { } picture && !page.Request.ExplicitSize && page.Pane is null)
            {
                float factor = Math.Min(Math.Max(1, 240f * Scale / picture.Width), Math.Min(width * Scale * .8f / picture.Width, height * Scale * .8f / picture.Height));
                float imageWidth = picture.Width * factor, imageHeight = picture.Height * factor;
                box = (x * Scale + (width * Scale - imageWidth) / 2, y * Scale + (height * Scale - imageHeight) / 2, imageWidth, imageHeight);
            }
            bool visible = paneVisible && ReferenceEquals(owner, _active) && !page.Hidden && _cover is null &&
                !_dashboardOpen && _palette == PaletteKind.None && !_setOpen && !_helpOpen && !_mruWalking && !NativeDialogOpen;
            if (page.Visible && !visible && page.Host.HasFocus && ReferenceEquals(owner, _active)) page.ResumeFocus = true;
            if (!ReferenceEquals(owner, _active)) page.ResumeFocus = false;
            page.Bounds = new Rectangle((int)box.x, (int)box.y, (int)box.w, (int)box.h);
            page.Visible = visible;
            var documentBounds = PresentWebNavigation(page);
            if (!page.RecreateHost) page.Host.Present(documentBounds, page.Visible && page.State != "failed");
            if (page.State == "loaded" && !ReferenceEquals(page.AppliedTheme, _theme)) ApplyWebTheme(page);
            if (page.Visible && page.ResumeFocus) { page.ResumeFocus = false; page.Host.Focus(); }
        }
    }

    private void WebAccelerator(Ses owner, WebPage page, CoreWebView2AcceleratorKeyPressedEventArgs e)
    {
        if (e.KeyEventKind is not (CoreWebView2KeyEventKind.KeyDown or CoreWebView2KeyEventKind.SystemKeyDown)) return;
        page.ExternalPromptsSilenced = false;
        bool ctrl = (Win32.GetKeyState(0x11) & 0x8000) != 0;
        bool shift = (Win32.GetKeyState(0x10) & 0x8000) != 0;
        bool alt = (Win32.GetKeyState(0x12) & 0x8000) != 0;
        uint key = e.VirtualKey;
        string? chord = Keymap.ChordFor((int)key, ctrl, alt, shift);
        if (chord is null || !_keymap.TryGetValue(chord, out var action) || action is not
            ("action_palette" or "session_palette" or "custom_palette" or "close_cover" or "increase_font_size" or "decrease_font_size" or "reset_font_size" or
             "close_session" or "close_pane" or "next_session" or "previous_session")) return;
        e.Handled = true;
        // WebView2 accelerator callbacks are synchronous: schedule UI work outside the callback.
        Post(() =>
        {
            if (!OwnsWebPage(owner, page)) return;
            if (action is "increase_font_size" or "decrease_font_size" or "reset_font_size")
            {
                ChangeWebZoom(action == "increase_font_size" ? 1 : action == "decrease_font_size" ? -1 : 0);
                return;
            }
            page.ResumeFocus = true;
            Win32.SetFocus(_hwnd);
            if (action is "close_cover" or "close_session" or "close_pane")
            { if (owner.PageParentId is not null) CloseSessionInternal(owner); else CloseWebPage(owner, page.Pane); }
            else if (ctrl && !alt && key == 9 && Keymap.UsesMruTabWalk(_keymap, chord)) MruWalk(shift ? -1 : 1);
            else RunAction(action);
            UpdateWebPresentation();
        });
    }
}

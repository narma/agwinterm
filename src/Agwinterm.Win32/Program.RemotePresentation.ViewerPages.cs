using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Agwinterm.Core;
using Agwinterm.Pty;
using Microsoft.Web.WebView2.Core;

namespace Agwinterm.Win32;

internal partial class Program
{
    private sealed class ReplicaPage(PresentationReplica replica, AgtermPresentationSender sender, string id,
        string? file, string identity, string partition, AgtermPageTransport? transport)
    {
        public readonly PresentationReplica Replica = replica;
        public readonly AgtermPresentationSender Sender = sender;
        public readonly string Id = id, Identity = identity, StoragePartition = partition;
        public readonly string? FilePath = file;
        public readonly AgtermPageTransport? Transport = transport;
        public CancellationTokenSource Document = new();
        public readonly List<Stream> Streams = [];
        public long AppliedSequence;
        public long DocumentNumber;
        public AgtermPageDocumentRequest? ActiveDocument;
        public Task? DocumentReady;
        public string? LastState;
    }

    private bool ApplyReplicaPage(Ses session, PresentationReplica replica, AgtermPresentationFrame frame, Dictionary<Guid, string> mapping)
    {
        if (frame.Kind is not ("page.request" or "page.close" or "page.command")) return false;
        if (replica.Sender is not { Granted: true } sender || !PageKinds.All(sender.Kinds.Contains)) return true;
        var body = frame.Body;
        string id = body.GetProperty("page").GetString()!;
        if (!Guid.TryParse(id, out _)) throw new IOException("Invalid remote page ID");
        var existing = _webPages.Select(p => p.Value).SingleOrDefault(p => p.Replica is { } remote
            && ReferenceEquals(remote.Replica, replica) && ReferenceEquals(remote.Sender, sender)
            && remote.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (frame.Kind == "page.close") { if (existing is not null) CloseWebPage(session, existing.Pane); return true; }
        if (frame.Kind == "page.command")
        {
            if (existing?.Replica is not { } remote) return true;
            long seq = body.GetProperty("seq").GetInt64();
            if (seq <= remote.AppliedSequence) return true;
            try
            {
                string action = body.GetProperty("action").GetString()!;
                remote.AppliedSequence = seq;
                if (action is "reloadOriginal" or "reloadCurrent" or "back" or "forward")
                {
                    existing.BridgeToken = null;
                    ResetReplicaDocument(existing);
                    BeginReplicaDocument(existing);
                }
                switch (action)
                {
                    case "hide": existing.Hidden = true; break;
                    case "show": existing.Hidden = false; break;
                    case "resize":
                        if (existing.Pane is not null) throw new ArgumentException("Pane page is full-pane");
                        existing.Geometry = new(PageSize(body, "sizePercent", 100) ?? 0, PageSize(body, "width", 16384), PageSize(body, "height", 16384)); break;
                    case "reloadOriginal": case "reloadCurrent": ReloadWebPage(session, existing, action == "reloadCurrent"); break;
                    case "back": if (existing.Host.Page.CanGoBack) existing.Host.Page.GoBack(); else RebindReplicaDocument(existing); break;
                    case "forward": if (existing.Host.Page.CanGoForward) existing.Host.Page.GoForward(); else RebindReplicaDocument(existing); break;
                    case "browser": OpenWebInBrowser(existing); break;
                    default: throw new ArgumentException("Unsupported page command");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { existing.Error = "Page operation unavailable"; }
            remote.AppliedSequence = seq; UpdateWebPresentation(); RequestRedraw();
            return true;
        }
        if (existing is not null) return true;
        WebPage? page = null;
        try
        {
            _ = WebViewHost.RuntimeVersion();
            string token = body.GetProperty("token").GetString()!;
            if (token.Length is < 1 or > 256 || token.Any(char.IsControl)) throw new IOException("Invalid page grant");
            string identity = body.GetProperty("identity").GetString()!;
            if (identity.Length > 8192) throw new IOException("Page identity exceeds limit");
            var source = body.GetProperty("source");
            if (source.EnumerateObject().Count() != 1) throw new IOException("Ambiguous page source");
            string? file = null, url = null;
            if (source.TryGetProperty("file", out var value))
            {
                file = value.GetProperty("path").GetString();
                if (file is null || !file.StartsWith('/') || file.Any(char.IsControl)) throw new IOException("Invalid origin path");
                if (!sender.Kinds.Contains(AgtermPageDocument.Capability))
                    throw new IOException("Remote file bridge requires page.bridge-revision support");
            }
            else url = source.GetProperty("url").GetProperty("_0").GetString();
            Pane? pane = null;
            if (body.TryGetProperty("pane", out var address) && address.ValueKind != JsonValueKind.Null)
            {
                if (!Guid.TryParse(address.GetString(), out var origin) || !mapping.TryGetValue(origin, out var local)) throw new IOException("Unmapped page pane");
                pane = session.Panes.SingleOrDefault(p => p.Id == local) ?? throw new IOException("Hidden page pane");
            }
            var fields = new Dictionary<string, object?>
            {
                [file is null ? "url" : "html"] = file ?? url,
                ["js"] = body.GetProperty("javascript").GetBoolean(), ["navigation"] = body.GetProperty("navigation").GetBoolean(),
                ["chromeless"] = body.GetProperty("chromeless").GetBoolean(), ["browse"] = body.GetProperty("browse").GetBoolean(),
                ["persistent"] = body.GetProperty("persistent").GetBoolean(), ["follow"] = body.GetProperty("follow").GetBoolean()
            };
            foreach (var (wire, local) in new[] { ("sizePercent", "size-percent"), ("width", "width"), ("height", "height"), ("backgroundColor", "background-color") })
                if (body.TryGetProperty(wire, out var option) && option.ValueKind != JsonValueKind.Null) fields[local] = option.Clone();
            var request = WebOverlayRequest.Parse(JsonSerializer.SerializeToElement(fields));
            if (pane is not null && request.ExplicitSize || url is not null && new Uri(url).IsLoopback)
                throw new IOException("Unsupported remote page placement or loopback URL");
            var slot = pane?.Overlay ?? session.Overlay;
            if (slot.Term is not null || slot.OriginJob?.Occupied == true || _webPages.ContainsKey((object?)pane ?? session)) throw new IOException("Page slot occupied");
            var binding = replica.Bindings[0];
            string partition = AgtermPageStorage.Partition(binding);
            page = new(request, NewWebHost(), session, pane);
            var transport = file is null ? null : new AgtermPageTransport(binding, token);
            page.Replica = new(replica, sender, id, file, identity, partition, transport);
            if (!_webPages.TryReserve(page.Slot, page, true, false)) throw new IOException("Page slot unavailable");
            WebOutcomes.Register(page.Id);
            if (transport is not null) _ = ObserveReplicaPageChannel(page, transport);
            StartWebPage(session, page);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or JsonException or KeyNotFoundException)
        {
            if (page is not null)
            {
                if (OwnsWebPage(session, page)) CloseWebPage(session, page.Pane);
                else { EndReplicaPage(page); page.Host.Dispose(); }
            }
            sender.Send("page.rejected", JsonSerializer.SerializeToElement(new { page = id, reason = "Page source, runtime or slot unavailable" }));
        }
        return true;
    }

    private static int? PageSize(JsonElement body, string name, int max)
    {
        if (!body.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        int size = value.GetInt32();
        if (size < 0 || size > max || size == 0 && name != "sizePercent") throw new ArgumentException("Invalid page size");
        return size;
    }

    private async Task ObserveReplicaPageChannel(WebPage page, AgtermPageTransport transport)
    {
        try { await transport.Completion.ConfigureAwait(false); } catch { }
        Post(() => { if (OwnsWebPage(page.Owner, page)) CloseWebPage(page.Owner, page.Pane); });
    }

    private static void ResetReplicaDocument(WebPage page)
    {
        if (page.Replica is not { } remote) return;
        remote.Document.Cancel(); remote.Document.Dispose(); remote.Document = new();
        foreach (var stream in remote.Streams) stream.Dispose();
        remote.Streams.Clear();
    }

    private static void BeginReplicaDocument(WebPage page)
    {
        if (page.Replica is not { Transport: { } transport } remote) return;
        var document = new AgtermPageDocumentRequest(checked(++remote.DocumentNumber), remote.AppliedSequence, null);
        remote.ActiveDocument = document;
        remote.DocumentReady = Activate();
        // Observe failed activation even when the page never invokes its bridge.
        _ = remote.DocumentReady.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        async Task Activate()
        {
            string answer = await transport.Client.BridgeAsync(document.Encode(), remote.Document.Token);
            using var response = JsonDocument.Parse(answer);
            if (!response.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                throw new IOException("Origin refused document activation");
        }
    }

    private static void RebindReplicaDocument(WebPage page)
    {
        page.BridgeToken = Guid.NewGuid().ToString("N");
        page.Host.Page.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "review-bind", token = page.BridgeToken }));
    }

    private void EndReplicaPage(WebPage page)
    {
        if (page.Replica is not { } remote) return;
        ResetReplicaDocument(page); remote.Document.Cancel();
        if (remote.Transport is not null) _ = remote.Transport.DisposeAsync();
        try { remote.Sender.Send("page.closed", JsonSerializer.SerializeToElement(new { page = remote.Id })); } catch (IOException) { }
    }

    private void CloseReplicaPages(PresentationReplica replica)
    {
        foreach (var page in _webPages.Select(p => p.Value).Where(p => ReferenceEquals(p.Replica?.Replica, replica)).ToArray())
            CloseWebPage(page.Owner, page.Pane);
    }

    private void PublishReplicaPageState(WebPage page)
    {
        if (page.Replica is not { } remote || !remote.Sender.Granted) return;
        static string? Bounded(string? value, int size) => value?.Length > size ? value[..size] : value;
        string? address = page.CurrentUrl;
        if (remote.FilePath is not null)
            address = Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Host == $"page-{page.Id}.invalid"
                ? Uri.UnescapeDataString(uri.AbsolutePath) : remote.FilePath;
        var state = JsonSerializer.SerializeToElement(new { page = remote.Id, title = Bounded(page.Title, 1024), address = Bounded(address, 8192),
            load = page.State, error = Bounded(page.Error, 1024), canGoBack = page.CanGoBack, canGoForward = page.CanGoForward,
            hidden = page.Hidden, appliedSeq = remote.AppliedSequence });
        string encoded = state.GetRawText();
        if (remote.LastState == encoded) return;
        try { remote.Sender.Send("page.state", state); remote.LastState = encoded; } catch (IOException) { }
    }

    private void BindReplicaResources(WebPage page, string origin)
    {
        var host = page.Host; var browser = host.Page; var remote = page.Replica!;
        browser.AddWebResourceRequestedFilter(origin + "/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        browser.WebResourceRequested += (_, e) =>
        {
            var deferral = e.GetDeferral();
            long generation = page.DocumentGeneration;
            var ct = remote.Document.Token;
            _ = host.RunAsync(async () =>
            {
                FileStream? stream = null;
                AgtermPageResponseStream? response = null;
                try
                {
                    string path = Uri.UnescapeDataString(new Uri(e.Request.Uri).AbsolutePath);
                    string temporary = Path.Combine(Path.GetTempPath(), "agwinterm-page-" + Guid.NewGuid().ToString("N"));
                    stream = new(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 65536,
                        FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                    string mime = await remote.Transport!.Client.FetchToAsync(path, stream, ct);
                    if (!OwnsWebPage(page.Owner, page) || page.DocumentGeneration != generation || !ReferenceEquals(page.Host, host)) return;
                    if (mime.Any(char.IsControl)) throw new IOException("Invalid page content type");
                    stream.Position = 0;
                    // Network waits finish before the browser starts reading the temporary response.
                    string policy = "object-src 'none'; frame-src 'none'; worker-src 'none'" + (!page.Request.JavaScript ? "; script-src 'none'" : "");
                    response = new(stream, () => Post(() => remote.Streams.Remove(response!)));
                    remote.Streams.Add(response); stream = null;
                    e.Response = browser.Environment.CreateWebResourceResponse(response, 200, "OK",
                        "Content-Type: " + mime + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: " + policy);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or OperationCanceledException or InvalidOperationException or COMException)
                {
                    response?.Dispose();
                    if (OwnsWebPage(page.Owner, page) && page.DocumentGeneration == generation && ReferenceEquals(page.Host, host))
                        try { e.Response = browser.Environment.CreateWebResourceResponse(Stream.Null, 403, "Resource unavailable", "Cache-Control: no-store"); }
                        catch (COMException) { }
                }
                finally { stream?.Dispose(); try { deferral.Complete(); } catch (COMException) { } }
            });
        };
    }

    private async Task BindReplicaCommands(WebPage page, string source)
    {
        var host = page.Host; var browser = host.Page; var remote = page.Replica!;
        bool Current(long generation) => OwnsWebPage(page.Owner, page) && ReferenceEquals(page.Host, host) && page.DocumentGeneration == generation;
        string origin = new Uri(source).GetLeftPart(UriPartial.Authority);
        bool Authorized(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.GetLeftPart(UriPartial.Authority) == origin;
        browser.Settings.IsWebMessageEnabled = true;
        await browser.AddScriptToExecuteOnDocumentCreatedAsync(ReviewScript(source, sameOrigin: true) + DeclarativeWebScript);
        browser.DOMContentLoaded += (_, _) =>
        {
            if (!Current(page.DocumentGeneration) || !Authorized(browser.Source)) return;
            page.BridgeToken = Guid.NewGuid().ToString("N");
            browser.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "review-bind", token = page.BridgeToken }));
        };
        browser.WebMessageReceived += (_, e) =>
        {
            long generation = page.DocumentGeneration;
            string? id = null;
            try
            {
                if (!Current(generation) || page.BridgeToken is null || !Authorized(e.Source)) throw new ArgumentException("Stale page bridge");
                var request = WebBridgeRequest.Parse(e.WebMessageAsJson, page.BridgeToken); id = request.Id;
                if (request.Target is not null || request.Window is not null) throw new ArgumentException("Remote page routing overrides are refused");
                string json = JsonSerializer.Serialize(new { cmd = request.Method, args = request.Args });
                _ = AgtermPageBridge.Parse(json, false);
                var ct = remote.Document.Token;
                var document = remote.ActiveDocument ?? throw new IOException("Document not activated");
                var ready = remote.DocumentReady ?? throw new IOException("Document not activated");
                _ = host.RunAsync(async () =>
                {
                    string response;
                    try
                    {
                        await ready.WaitAsync(ct);
                        ct.ThrowIfCancellationRequested();
                        response = await remote.Transport!.Client.BridgeAsync((document with { Request = json }).Encode(), ct);
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or InvalidOperationException)
                    { response = "{\"ok\":false,\"error\":\"Page request unavailable\"}"; }
                    if (!Current(generation)) return;
                    try
                    {
                        using var doc = JsonDocument.Parse(response);
                        var reply = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
                        reply["id"] = request.Id;
                        browser.PostWebMessageAsJson(JsonSerializer.Serialize(reply));
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
                    {
                        browser.PostWebMessageAsJson(JsonSerializer.Serialize(new { id = request.Id, ok = false, error = "Invalid origin response" }));
                    }
                });
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or JsonException or InvalidOperationException)
            {
                if (Current(generation)) browser.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, ok = false, error = "Command outside remote page grant" }));
            }
        };
    }
}

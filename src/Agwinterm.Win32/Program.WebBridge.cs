using System.Text.Json;
using Agwinterm.Core;
using Microsoft.Web.WebView2.Core;

namespace Agwinterm.Win32;

internal partial class Program
{
    private static string ReviewScript(string source, bool sameOrigin = false) => $$$"""
        (() => {
          if (window !== top || {{{(sameOrigin ? "location.origin" : "location.href.split('#')[0]")}}} !== {{{JsonSerializer.Serialize(sameOrigin ? new Uri(source).GetLeftPart(UriPartial.Authority) : source)}}}) return;
          let token, sequence = 0, bind;
          const ready = new Promise(resolve => bind = resolve), pending = new Map();
          chrome.webview.addEventListener('message', event => {
            const reply = event.data;
            if (reply.kind === 'review-bind') { token = reply.token; bind(); return; }
            const waiter = pending.get(reply.id); if (!waiter) return;
            pending.delete(reply.id); reply.ok ? waiter.resolve(reply.result) : waiter.reject(new Error(reply.error));
          });
          const api = Object.freeze({request(method, options = {}) {
            return ready.then(() => new Promise((resolve, reject) => {
              const id = String(++sequence); pending.set(id, {resolve,reject});
              try { chrome.webview.postMessage({id, token, method, options}); }
              catch (error) { pending.delete(id); reject(error); }
            }));
          }});
          Object.defineProperty(window, 'agwinterm', {value:api});
          Object.defineProperty(window, 'agterm', {value:api});
        })();
        """;

    private async Task BindReview(Ses owner, WebPage page, string source)
    {
        var host = page.Host;
        bool IsCurrent() => !owner.Closing && OwnsWebPage(owner, page) && ReferenceEquals(page.Host, host);
        var browser = host.Page;
        browser.Settings.IsWebMessageEnabled = true;
        await browser.AddScriptToExecuteOnDocumentCreatedAsync(ReviewScript(source));
        if (!IsCurrent()) return;
        browser.DOMContentLoaded += (_, _) =>
        {
            if (!IsCurrent() || browser.Source.Split('#')[0] != source) return;
            page.BridgeToken = Guid.NewGuid().ToString("N");
            browser.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "review-bind", token = page.BridgeToken }));
        };
        // CoreWebView2.WebMessageReceived receives the top-level document only.
        // Frame.WebMessageReceived is never subscribed; local review CSP also refuses frames.
        browser.WebMessageReceived += (_, e) =>
        {
            string? id = null;
            try
            {
                if (!IsCurrent() || e.Source.Split('#')[0] != source || page.BridgeToken is null)
                    throw new ArgumentException("review document is no longer authorized");
                string json = e.WebMessageAsJson;
                var request = ReviewBridgeRequest.Parse(json, page.BridgeToken);
                id = request.Id;
                if (!CompleteWebOutcome(page, request.Value)) throw new ArgumentException("page has already completed");
                browser.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, ok = true, result = new { pageID = page.Id } }));
                Post(() => { if (OwnsWebPage(owner, page)) CloseWebPage(owner, page.Pane); });
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException)
            {
                if (!IsCurrent()) return;
                if (id is null && e.WebMessageAsJson.Length <= 6 * ReviewBridgeRequest.MaxValueBytes + 4096)
                {
                    try
                    {
                        using var failed = JsonDocument.Parse(e.WebMessageAsJson, new JsonDocumentOptions { MaxDepth = 8 });
                        if (failed.RootElement.ValueKind == JsonValueKind.Object && failed.RootElement.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 64) id = value.GetString();
                    }
                    catch (JsonException) { }
                }
                browser.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, ok = false, error = ex.Message }));
            }
        };
    }
}

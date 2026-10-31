using System.Text.Json;
using System.Runtime.InteropServices;
using Agwinterm.Pty;

namespace Agwinterm.Win32;

internal partial class Program
{
    private async Task BindLocalResources(WebPage page, string origin)
    {
        var host = page.Host;
        var browser = host.Page;
        bool Current() => OwnsWebPage(page.Owner, page) && ReferenceEquals(page.Host, host);
        using var tree = JsonDocument.Parse(await browser.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}"));
        string mainFrame = tree.RootElement.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString()!;
        string fetchOptions = JsonSerializer.Serialize(new
        { patterns = new[] { new { urlPattern = origin + "/*", requestStage = "Request" } } });
        const string attachOptions = """{"autoAttach":true,"waitForDebuggerOnStart":true,"flatten":true}""";
        browser.GetDevToolsProtocolEventReceiver("Target.attachedToTarget").DevToolsProtocolEventReceived += (_, e) =>
        {
            using var attached = JsonDocument.Parse(e.ParameterObjectAsJson);
            string session = attached.RootElement.GetProperty("sessionId").GetString()!;
            _ = host.RunAsync(async () =>
            {
                try
                {
                    // Opaque-origin frames can run in separate renderers. Install their
                    // resource handler before resuming scripts; recurse for nested frames/workers.
                    await browser.CallDevToolsProtocolMethodForSessionAsync(session, "Fetch.enable", fetchOptions);
                    await browser.CallDevToolsProtocolMethodForSessionAsync(session, "Target.setAutoAttach", attachOptions);
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
                finally
                {
                    try { await browser.CallDevToolsProtocolMethodForSessionAsync(session, "Runtime.runIfWaitingForDebugger", "{}"); }
                    catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
                }
            });
        };
        // WebResourceRequested does not expose frame identity, and Sec-Fetch-Dest is
        // not guaranteed to be present there. Fetch supplies the browser-owned frameId,
        // even when the same URL is loaded both as the top document and as a child.
        browser.GetDevToolsProtocolEventReceiver("Fetch.requestPaused").DevToolsProtocolEventReceived += (_, e) =>
        {
            using var message = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = message.RootElement;
            string id = root.GetProperty("requestId").GetString()!;
            string session = e.SessionId;
            string url = root.GetProperty("request").GetProperty("url").GetString()!;
            bool child = root.GetProperty("resourceType").GetString() == "Document" &&
                root.GetProperty("frameId").GetString() != mainFrame;
            _ = host.RunAsync(async () =>
            {
                try
                {
                    byte[] bytes;
                    string path = new Uri(url).AbsolutePath;
                    int status = 200;
                    try { bytes = page.ImageDocument ?? await Task.Run(() => page.Resources!.Read(path)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                    { bytes = []; status = 403; }
                    if (!Current()) return;
                    string policy = "object-src 'none'" + (child ? "; sandbox allow-scripts allow-forms" : "") +
                        (!page.Request.JavaScript ? "; script-src 'none'" : "") +
                        (page.Request.Bridge is not null ? "; frame-src 'none'" : "");
                    await browser.CallDevToolsProtocolMethodForSessionAsync(session, "Fetch.fulfillRequest", JsonSerializer.Serialize(new
                    {
                        requestId = id, responseCode = status,
                        responseHeaders = new[] {
                            new { name = "Content-Type", value = page.ImageDocument is null ? LocalWebSource.ContentType(path) : "text/html; charset=utf-8" },
                            new { name = "Cache-Control", value = "no-store" },
                            new { name = "X-Content-Type-Options", value = "nosniff" },
                            new { name = "Content-Security-Policy", value = policy }
                        },
                        body = Convert.ToBase64String(bytes)
                    }));
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException)
                { /* Navigation or host disposal can cancel a paused request. */ }
            });
        };
        await browser.CallDevToolsProtocolMethodAsync("Fetch.enable", fetchOptions);
        await browser.CallDevToolsProtocolMethodAsync("Target.setAutoAttach", attachOptions);
    }
}

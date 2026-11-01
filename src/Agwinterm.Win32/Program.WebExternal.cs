using Agwinterm.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Agwinterm.Win32;

internal partial class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WebLastInput { public uint Size; public uint Tick; }
    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref WebLastInput info);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryStringW(uint flags, uint str, string association, string? extra, StringBuilder output, ref uint length);

    private static uint WebInputTick()
    {
        var input = new WebLastInput { Size = (uint)Marshal.SizeOf<WebLastInput>() };
        return GetLastInputInfo(ref input) ? input.Tick : 0;
    }

    private static void OpenWebInBrowser(WebPage page)
    {
        if (page.Resources is null)
        {
            string destination = page.CommittedUrl ?? page.CurrentUrl ?? page.Request.Url!;
            if (!Uri.TryCreate(destination, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
                throw new ArgumentException("The shown page has no HTTP(S) address to open in a browser.");
            Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true });
            return;
        }
        // A user's .html association may be an editor. Resolve the HTTP browser,
        // then pass the file as one argument, never as an interpreted command line.
        var executable = new StringBuilder(32768);
        uint length = (uint)executable.Capacity;
        if (AssocQueryStringW(0x1000 /* IS_PROTOCOL */, 2 /* EXECUTABLE */, "http", "open", executable, ref length) != 0)
            throw new InvalidOperationException("Cannot resolve the default HTTP browser. Configure a default browser in Windows Settings.");
        var start = new ProcessStartInfo(executable.ToString()) { UseShellExecute = false };
        start.ArgumentList.Add(page.Resources.ExternalPath);
        Process.Start(start);
    }

    private void RevealWebFile(WebPage page)
    {
        if (page.Resources is null) throw new ArgumentException("Show in Explorer requires a local file page");
        string current = page.CommittedUrl ?? OriginalWebUrl(page);
        var original = new Uri(OriginalWebUrl(page));
        var shown = new Uri(current);
        if (shown.GetLeftPart(UriPartial.Authority) != original.GetLeftPart(UriPartial.Authority))
            throw new ArgumentException("The shown page is not a local file");
        _ = Task.Run(() =>
        {
            try
            {
                string path = page.Resources.ResolveExternalPath(shown.AbsolutePath);
                PostVerb(() =>
                {
                    if (!OwnsWebPage(page.Owner, page) || page.Owner.Closing) return;
                    try
                    {
                        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                        start.ArgumentList.Add("/select," + path);
                        Process.Start(start);
                    }
                    catch (Exception ex) { page.Error = ex.Message; RequestRedraw(); }
                });
            }
            catch (Exception ex) { PostVerb(() => { if (OwnsWebPage(page.Owner, page)) { page.Error = ex.Message; RequestRedraw(); } }); }
        });
    }

    // Run outside WebView2 callbacks; a synthetic link must not launch another app
    // without a separate native confirmation. Cancel is the first/default button.
    private void OfferWebExternal(Ses owner, WebPage page, string destination)
    {
        RefreshWebExternalInput(page);
        if (!Uri.TryCreate(destination, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 ||
            page.ExternalPromptsSilenced || page.ExternalPromptQueued || page.ExternalPromptId is not null || !page.Visible) return;
        page.ExternalPromptQueued = true;
        long generation = page.DocumentGeneration;
        PostVerb(() =>
        {
            page.ExternalPromptQueued = false;
            if (!OwnsWebPage(owner, page) || !page.Visible || page.ExternalPromptsSilenced ||
                generation != page.DocumentGeneration || NativeDialogOpen) return;
            try
            {
                string id = "";
                id = OpenAskCore(new AskSpec("Open in browser?", destination, [],
                    [new("cancel", "Cancel", null), new("open", "Open in browser", null)]), false,
                    () => OwnsWebPage(owner, page) && page.Visible, outcome =>
                    {
                        if (page.ExternalPromptId != id) return;
                        page.ExternalPromptId = null;
                        if (outcome.Result != "answered" || outcome.Button != "open")
                        {
                            page.ExternalPromptsSilenced = true;
                            page.ExternalDeniedInputTick = WebInputTick();
                            return;
                        }
                        if (!OwnsWebPage(owner, page) || page.Hidden || !ReferenceEquals(owner, _active) || generation != page.DocumentGeneration) return;
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(destination) { UseShellExecute = true }); }
                        catch (Exception ex) { page.Error = ex.Message; RequestRedraw(); }
                    });
                page.ExternalPromptId = id;
            }
            catch (InvalidOperationException) { /* A competing native operation keeps ownership. */ }
        });
    }

    private void RefreshWebExternalInput(WebPage page)
    {
        // DOM-dispatched events do not advance Windows' last-input timestamp.
        // Only reset while this browser owns focus, never for background timers.
        if (page.ExternalPromptsSilenced && GetForegroundWindow() == _hwnd &&
            page.Host.HasFocus && WebInputTick() != page.ExternalDeniedInputTick)
            page.ExternalPromptsSilenced = false;
    }

    private void CancelWebExternal(WebPage page)
    {
        if (page.ExternalPromptId is not { } id) return;
        page.ExternalPromptId = null;
        AbortAsk(id);
    }
}

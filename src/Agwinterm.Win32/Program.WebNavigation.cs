using System.Drawing;
using System.Text.Json;

namespace Agwinterm.Win32;

internal partial class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnableWindow(IntPtr window, bool enabled);
    private bool WebNavigationKey(Win32.MSG message)
    {
        if (message.message != 0x100) return false;
        foreach (var (_, page) in _webPages)
        {
            var owner = page.Owner;
            int index = Array.IndexOf(page.NavigationButtons, message.hwnd);
            if (index < 0) continue;
            int key = (int)message.wParam;
            if (key == 27) { if (owner.PageParentId is not null) CloseSessionInternal(owner); else CloseWebPage(owner, page.Pane); return true; }
            if (key == 13) { WebNavigationClick(message.hwnd); return true; }
            if (key != 9) return false;
            bool previous = (Win32.GetKeyState(0x10) & 0x8000) != 0;
            var order = WebNavigationOrder(page).ToArray();
            int next = Array.IndexOf(order, message.hwnd) + (previous ? -1 : 1);
            if (next < 0 || next >= order.Length)
                page.Host.Focus(previous ? Microsoft.Web.WebView2.Core.CoreWebView2MoveFocusReason.Previous : Microsoft.Web.WebView2.Core.CoreWebView2MoveFocusReason.Next);
            else Win32.SetFocus(order[next]);
            return true;
        }
        return false;
    }

    private void CreateWebNavigation(WebPage page)
    {
        if (!page.Request.Chromeless && page.OriginLabel == IntPtr.Zero)
            page.OriginLabel = Win32.CreateWindowExW(0, "STATIC", "", Win32.WS_CHILD | 0x04000000 | 0xD /* SS_OWNERDRAW | WS_CLIPSIBLINGS */,
                0, 0, 1, 1, _hwnd, IntPtr.Zero, Win32.GetModuleHandleW(null), IntPtr.Zero);
        if (page.ErrorLabel == IntPtr.Zero)
            page.ErrorLabel = Win32.CreateWindowExW(0, "STATIC", "", Win32.WS_CHILD | 0x80 /* SS_NOPREFIX */,
                0, 0, 1, 1, _hwnd, IntPtr.Zero, Win32.GetModuleHandleW(null), IntPtr.Zero);
        if (page.Request.Chromeless || page.NavigationButtons.Length != 0) return;
        string[] labels = page.Request.Navigation
            ? ["Back", "Forward", "Reload", "Open in browser", "Close", "Hide", page.Resources is null ? "Copy link" : "Show in Explorer"]
            : ["Close", "Hide"];
        page.NavigationButtons = labels
            .Select((label, index) => Win32.CreateWindowExW(0, "BUTTON", label,
                Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_TABSTOP | 0xB /* BS_OWNERDRAW */, 0, 0, 1, 1, _hwnd,
                (IntPtr)(7100 + index), Win32.GetModuleHandleW(null), IntPtr.Zero)).ToArray();
        CreateWebTooltips(page, labels);
    }

    private Rectangle PresentWebNavigation(WebPage page)
    {
        var bounds = page.Bounds;
        bool moved = page.NativeBounds != bounds;
        int height = page.NavigationButtons.Length == 0 ? 0 : Math.Min(bounds.Height, (int)Math.Ceiling(36 * Scale));
        int buttonWidth = (int)Math.Ceiling(32 * Scale);
        if (page.OriginLabel != IntPtr.Zero)
        {
            var shown = new Uri(page.CommittedUrl ?? OriginalWebUrl(page));
            string identity = page.Resources is not null ? Path.GetFileName(page.Resources.ExternalPath) :
                shown.AbsoluteUri;
            string origin = string.IsNullOrWhiteSpace(page.Title) ? identity : identity + "  ·  " + page.Title;
            if (page.PresentedOrigin != origin) Win32.SetWindowTextW(page.OriginLabel, origin);
            page.PresentedOrigin = origin;
            if (moved) Win32.SetWindowPos(page.OriginLabel, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, height, 0x0014);
            if (page.NavigationVisible != page.Visible) Win32.ShowWindow(page.OriginLabel, page.Visible ? 4 : 0);
        }
        for (int i = 0; i < page.NavigationButtons.Length; i++)
        {
            if (page.Request.Navigation && i < 2) EnableWindow(page.NavigationButtons[i], i == 0 ? page.CanGoBack : page.CanGoForward);
            if (page.Request.Navigation && i == 2) EnableWindow(page.NavigationButtons[i], page.Host.IsReady || page.RecreateHost);
            if (page.Owner.PageParentId is not null && i == (page.Request.Navigation ? 5 : 1)) { Win32.ShowWindow(page.NavigationButtons[i], 0); continue; }
            int action = page.Request.Navigation ? i : i + 4;
            int left = action switch
            {
                4 => bounds.Right - buttonWidth,
                5 => bounds.Right - 2 * buttonWidth,
                6 => bounds.Left + 4 * buttonWidth,
                _ => bounds.Left + action * buttonWidth
            };
            if (moved) Win32.SetWindowPos(page.NavigationButtons[i], IntPtr.Zero, left, bounds.Top,
                buttonWidth, height, 0x0014 /* NOACTIVATE | NOZORDER */);
            bool visible = page.Visible && (action is 4 or 5 || left + buttonWidth <= bounds.Right - 2 * buttonWidth);
            Win32.ShowWindow(page.NavigationButtons[i], visible ? 4 : 0);
        }
        var document = new Rectangle(bounds.Left, bounds.Top + height, bounds.Width, Math.Max(0, bounds.Height - height));
        if (page.ErrorLabel != IntPtr.Zero)
        {
            string error = page.Error ?? "Page unavailable";
            if (page.PresentedError != error)
                Win32.SetWindowTextW(page.ErrorLabel, "Web page failed: " + error[..Math.Min(512, error.Length)] + "\r\nReload to retry, or close the overlay.");
            if (moved) Win32.SetWindowPos(page.ErrorLabel, IntPtr.Zero, document.Left, document.Top, document.Width, document.Height, 0x0014);
            bool showError = page.Visible && page.State == "failed";
            if (page.ErrorVisible != showError) Win32.ShowWindow(page.ErrorLabel, showError ? 4 : 0);
            page.PresentedError = error;
            page.ErrorVisible = showError;
        }
        page.NativeBounds = bounds;
        page.NavigationVisible = page.Visible;
        return document;
    }

    private bool WebNavigationClick(IntPtr control)
    {
        if (control == IntPtr.Zero) return false;
        foreach (var (_, page) in _webPages)
        {
            var owner = page.Owner;
            int index = Array.IndexOf(page.NavigationButtons, control);
            if (index < 0) continue;
            if (!page.Request.Navigation) index += 4;
            if (index == 4) { if (owner.PageParentId is not null) CloseSessionInternal(owner); else CloseWebPage(owner, page.Pane); return true; }
            if (index == 5) { page.Hidden = true; UpdateWebPresentation(); RequestRedraw(); return true; }
            if (index == 2) { ReloadWebPage(owner, page); return true; }
            try
            {
                var browser = page.Host.Page;
                switch (index)
                {
                    case 0: if (browser.CanGoBack) browser.GoBack(); break;
                    case 1: if (browser.CanGoForward) browser.GoForward(); break;
                    case 2: ReloadWebPage(owner, page); break;
                    case 3:
                        OpenWebInBrowser(page);
                        break;
                    case 6:
                        if (page.Resources is null) ClipboardSet(page.CurrentUrl ?? page.Request.Url!);
                        else RevealWebFile(page);
                        break;
                }
                page.Host.Focus();
            }
            catch (Exception ex) { page.Error = ex.Message; RequestRedraw(); }
            return true;
        }
        return false;
    }

    private void ApplyWebTheme(WebPage page, bool documentLoaded = false)
    {
        var variables = new Dictionary<string, string>();
        static string Css(Agwinterm.Core.Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        foreach (string prefix in new[] { "--agwinterm-", "--agterm-" })
        {
            variables[prefix + "background"] = page.Request.Background ?? Css(_theme.DefaultBackground);
            variables[prefix + "foreground"] = Css(_theme.DefaultForeground);
            for (int i = 0; i < 16; i++) variables[prefix + "color-" + i] = Css(_theme.Palette[i]);
        }
        page.AppliedTheme = _theme;
        if (page.OriginLabel != IntPtr.Zero) Win32.InvalidateRect(page.OriginLabel, IntPtr.Zero, false);
        foreach (var button in page.NavigationButtons) Win32.InvalidateRect(button, IntPtr.Zero, false);
        string colorScheme = (_theme.DefaultBackground.R * 299 + _theme.DefaultBackground.G * 587 + _theme.DefaultBackground.B * 114) >= 128000 ? "light" : "dark";
        _ = page.Host.RunAsync(async () =>
        {
            try
            {
                string script = "(() => {const apply=()=>{const s=document.documentElement?.style; if(s) for(const [k,v] of Object.entries(" +
                    JsonSerializer.Serialize(variables) + ")) s.setProperty(k,v);" +
                    (page.Resources is null ? "" : "if(document.head){let style=document.getElementById('agwinterm-default-theme');if(!style){style=document.createElement('style');style.id='agwinterm-default-theme';document.head.prepend(style);}style.textContent=':where(html,body){background:var(--agterm-background);color:var(--agterm-foreground);color-scheme:" + colorScheme + "}';}") +
                    "};apply();document.addEventListener('DOMContentLoaded',apply,{once:true});})()";
                if (page.ThemeScriptId is not null) page.Host.Page.RemoveScriptToExecuteOnDocumentCreated(page.ThemeScriptId);
                page.ThemeScriptId = await page.Host.Page.AddScriptToExecuteOnDocumentCreatedAsync(script);
                if (page.Resources is not null || documentLoaded) await page.Host.Page.ExecuteScriptAsync(script);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        });
    }
}

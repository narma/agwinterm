using System.Runtime.InteropServices;

namespace Agwinterm.Win32;

internal partial class Program
{
    [StructLayout(LayoutKind.Sequential)] private struct WebDrawItem
    {
        public uint Type, Control, Item, Action, State;
        public IntPtr Window, Dc;
        public Win32.RECT Bounds;
        public IntPtr Data;
    }
    [StructLayout(LayoutKind.Sequential)] private struct WebToolInfo
    {
        public uint Size, Flags;
        public IntPtr Window, Id;
        public Win32.RECT Bounds;
        public IntPtr Instance, Text, Param, Reserved;
    }
    [DllImport("user32.dll", EntryPoint = "FillRect")] private static extern int WebFillRect(IntPtr dc, ref Win32.RECT rect, IntPtr brush);
    [DllImport("user32.dll", EntryPoint = "DrawTextW", CharSet = CharSet.Unicode)] private static extern int WebDrawText(IntPtr dc, string text, int count, ref Win32.RECT rect, uint format);
    [DllImport("user32.dll", EntryPoint = "DrawFocusRect")] private static extern bool WebDrawFocus(IntPtr dc, ref Win32.RECT rect);
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")] private static extern bool WebControlVisible(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "IsWindowEnabled")] private static extern bool WebControlEnabled(IntPtr window);
    [DllImport("gdi32.dll", EntryPoint = "SelectObject")] private static extern IntPtr WebSelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll", EntryPoint = "SetBkMode")] private static extern int WebSetBkMode(IntPtr dc, int mode);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr WebAddTool(IntPtr window, uint message, IntPtr wParam, ref WebToolInfo info);

    private static IEnumerable<IntPtr> WebNavigationOrder(WebPage page)
    {
        int[] order = page.Request.Navigation ? [0, 1, 2, 3, 6, 5, 4] : [1, 0];
        foreach (int index in order)
            if (index < page.NavigationButtons.Length && WebControlVisible(page.NavigationButtons[index]) && WebControlEnabled(page.NavigationButtons[index]))
                yield return page.NavigationButtons[index];
    }

    private void CreateWebTooltips(WebPage page, string[] labels)
    {
        page.Tooltip = Win32.CreateWindowExW(8 /* TOPMOST */, "tooltips_class32", "", 0x80000003 /* POPUP | ALWAYSTIP | NOPREFIX */,
            0, 0, 0, 0, _hwnd, IntPtr.Zero, Win32.GetModuleHandleW(null), IntPtr.Zero);
        for (int i = 0; i < labels.Length && page.Tooltip != IntPtr.Zero; i++)
        {
            var text = Marshal.StringToHGlobalUni(labels[i]);
            page.TooltipText.Add(text);
            var info = new WebToolInfo { Size = (uint)Marshal.SizeOf<WebToolInfo>(), Flags = 0x11 /* IDISHWND | SUBCLASS */,
                Window = _hwnd, Id = page.NavigationButtons[i], Text = text };
            WebAddTool(page.Tooltip, 0x432 /* TTM_ADDTOOLW */, IntPtr.Zero, ref info);
        }
    }

    private static void DestroyWebTooltips(WebPage page)
    {
        if (page.Tooltip != IntPtr.Zero) Win32.DestroyWindow(page.Tooltip);
        foreach (var text in page.TooltipText) Marshal.FreeHGlobal(text);
        page.TooltipText.Clear();
        page.Tooltip = IntPtr.Zero;
    }

    private bool DrawWebChrome(IntPtr address)
    {
        if (address == IntPtr.Zero) return false;
        var draw = Marshal.PtrToStructure<WebDrawItem>(address);
        foreach (var (_, page) in _webPages)
        {
            bool title = draw.Window == page.OriginLabel;
            int index = Array.IndexOf(page.NavigationButtons, draw.Window);
            if (!title && index < 0) continue;
            var bg = _theme.DefaultBackground;
            var fg = _theme.DefaultForeground;
            int blend = !title && (draw.State & 1) != 0 ? 60 : 32;
            int Mix(int a, int b) => (a * (255 - blend) + b * blend) / 255;
            var brush = Win32.CreateSolidBrush(Win32.RGB(Mix(bg.R, fg.R), Mix(bg.G, fg.G), Mix(bg.B, fg.B)));
            try { WebFillRect(draw.Dc, ref draw.Bounds, brush); }
            finally { Win32.DeleteObject(brush); }
            string text;
            var rect = draw.Bounds;
            if (title)
            {
                text = page.PresentedOrigin ?? "";
                var layout = Agwinterm.Core.WebChromeLayout.Fit(page.Bounds.Width, Scale, page.Request.Navigation, page.Owner.PageParentId is not null);
                int labelOffset = layout.NavigationCount * layout.ButtonWidth;
                rect.left += layout.TitleLeft - labelOffset;
                rect.right = draw.Bounds.left + layout.TitleRight - labelOffset;
            }
            else
            {
                int action = page.Request.Navigation ? index : index + 4;
                text = action switch { 0 => "\uE72B", 1 => "\uE72A", 2 => "\uE72C", 3 => "\uE8A7", 4 => "\uE8BB", 5 => "\uE921", _ => page.Resources is null ? "\uE8C8" : "\uE8B7" };
            }
            var font = Win32.CreateFontW(-(int)Math.Ceiling((title ? 13 : 14) * Scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, title ? "Segoe UI" : "Segoe MDL2 Assets");
            var previous = WebSelectObject(draw.Dc, font);
            int oldMode = WebSetBkMode(draw.Dc, 1);
            int oldColor = Win32.SetTextColor(draw.Dc, !title && (draw.State & 4) != 0
                ? Win32.RGB((bg.R + fg.R) / 2, (bg.G + fg.G) / 2, (bg.B + fg.B) / 2) : Win32.RGB(fg.R, fg.G, fg.B));
            try
            {
                if (rect.right > rect.left) WebDrawText(draw.Dc, text, text.Length, ref rect, 0x8825 /* CENTER | VCENTER | SINGLELINE | NOPREFIX | END_ELLIPSIS */);
                if (!title && (draw.State & 0x10) != 0)
                {
                    rect.left += 4; rect.right -= 4; rect.top += 4; rect.bottom -= 4;
                    WebDrawFocus(draw.Dc, ref rect);
                }
            }
            finally
            {
                Win32.SetTextColor(draw.Dc, oldColor);
                WebSetBkMode(draw.Dc, oldMode);
                WebSelectObject(draw.Dc, previous);
                Win32.DeleteObject(font);
            }
            return true;
        }
        return false;
    }
}

using System.Globalization;
using System.Numerics;
using Agwinterm.Core;
using Agwinterm.Pty;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Agwinterm.Win32;

internal partial class Program
{
    private sealed class NativeHudLayout : IDisposable
    {
        public required IDWriteTextLayout Title, Detail;
        public required SessionHuds.Box Box;
        public required float SpinnerWidth, VerticalPadding, FontSize;
        public void Dispose() { Title.Dispose(); Detail.Dispose(); }
    }

    private NativeHudLayout BuildHudLayout(Ses session, HudSpec hud)
    {
        var (x, y, w, h) = ContentArea();
        if (hud.AnchorPaneId is { } anchor)
        {
            var pane = PaneLayout(session).FirstOrDefault(p => p.pane.Id == anchor);
            if (pane.pane is not null) (x, y, w, h) = (pane.x, pane.y, pane.w, pane.h);
        }
        float fontSize = (float)(hud.FontSize ?? 13), spinner = hud.Spinner == "none" ? 0 : fontSize * 24 / 13;
        float verticalPad = hud.Frame ? 12 : 6;
        using var font = NewChromeFormat("Segoe UI", fontSize, false);
        using var small = NewChromeFormat("Segoe UI", fontSize * 11.5f / 13, false);
        string body = hud.Document?.Text ?? hud.Message;
        IDWriteTextLayout Title(float width)
        {
            var layout = _dwrite.CreateTextLayout(body, font, width, 20000);
            layout.WordWrapping = WordWrapping.Wrap; layout.ParagraphAlignment = ParagraphAlignment.Near;
            if (hud.Document is { } doc)
                foreach (var span in doc.Ranges)
                {
                    var range = new TextRange((uint)span.Start, (uint)span.Length);
                    if (span.Bold) layout.SetFontWeight(FontWeight.Bold, range);
                    if (span.Italic) layout.SetFontStyle(FontStyle.Italic, range);
                    if (span.Code) layout.SetFontFamilyName("Consolas", range);
                    if (span.Size != 1) layout.SetFontSize(fontSize * span.Size, range);
                }
            return layout;
        }
        IDWriteTextLayout Detail(float width)
        {
            var layout = _dwrite.CreateTextLayout(hud.Detail ?? "", small, width, 20000);
            layout.WordWrapping = WordWrapping.Wrap; layout.ParagraphAlignment = ParagraphAlignment.Near;
            return layout;
        }
        float cap = hud.Sticky && hud.Position != "center" ? 1 : .8f;
        float maxWidth = Math.Max(1, w * cap - 24 - spinner);
        using var measure = Title(maxWidth);
        using var detailMeasure = Detail(maxWidth);
        float naturalWidth = Math.Max(measure.Metrics.Width, hud.Detail is null ? 0 : detailMeasure.Metrics.Width);
        var initial = SessionHuds.Place(x, y, w, h, naturalWidth + 24 + spinner, 0, hud);
        float textWidth = Math.Max(1, initial.Width - 24 - spinner);
        var title = Title(textWidth);
        try
        {
            var detail = Detail(textWidth);
            float height = title.Metrics.Height + (hud.Detail is null ? 0 : detail.Metrics.Height + 6) + 2 * verticalPad;
            return new() { Title = title, Detail = detail, SpinnerWidth = spinner, VerticalPadding = verticalPad, FontSize = fontSize,
                Box = SessionHuds.Place(x, y, w, h, naturalWidth + 24 + spinner, height, hud) };
        }
        catch { title.Dispose(); throw; }
    }

    private HudSpec? ProjectHud(Ses session)
    {
        ReconcileHud(session);
        if (session.Hud is not { } hud) return null;
        using var layout = BuildHudLayout(session, hud);
        var b = layout.Box;
        int x = (int)MathF.Round(b.X * Scale), y = (int)MathF.Round(b.Y * Scale);
        return hud with { EffectiveFontSize = layout.FontSize,
            Visible = ReferenceEquals(session, _active) && !_dashboardOpen && _cover is null,
            Geometry = new(x, y, Math.Max(0, (int)MathF.Round((b.X + b.Width) * Scale) - x), Math.Max(0, (int)MathF.Round((b.Y + b.Height) * Scale) - y)) };
    }

    private void DrawHud(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, Ses session)
    {
        ReconcileHud(session);
        if (session.Hud is not { } hud) return;
        using var layout = BuildHudLayout(session, hud);
        var box = layout.Box;
        if (box.Width < 2 || box.Height < 2) return;
        var rect = new Rect(box.X, box.Y, box.Width, box.Height);
        static Color4 ColorOf(string? hex, Color4 fallback)
        {
            if (hex is null) return fallback;
            uint n = uint.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new Color4(((n >> 16) & 255) / 255f, ((n >> 8) & 255) / 255f, (n & 255) / 255f, 1);
        }
        rt.PushAxisAlignedClip(rect, AntialiasMode.PerPrimitive);
        try
        {
            brush.Color = ColorOf(hud.BackgroundColor, C4(_theme.DefaultBackground)); rt.FillRectangle(rect, brush);
            if (hud.Frame) { brush.Color = ChromeAccent; rt.DrawRectangle(rect, brush, 1); }
            var fg = ColorOf(hud.TextColor, C4(_theme.DefaultForeground)); brush.Color = fg;
            if (layout.SpinnerWidth > 0)
            {
                using var font = NewChromeFormat("Segoe UI", layout.FontSize, false);
                rt.DrawText(SessionHuds.Frame(hud.Spinner, Environment.TickCount64), font,
                    new Rect(box.X + 10, box.Y + layout.VerticalPadding, layout.SpinnerWidth, layout.Title.Metrics.Height), brush, DrawTextOptions.Clip);
            }
            rt.DrawTextLayout(new Vector2(box.X + 12 + layout.SpinnerWidth, box.Y + layout.VerticalPadding), layout.Title, brush, DrawTextOptions.Clip);
            if (hud.Detail is not null)
            {
                brush.Color = WithA(fg, .75f);
                rt.DrawTextLayout(new Vector2(box.X + 12 + layout.SpinnerWidth, box.Y + layout.VerticalPadding + layout.Title.Metrics.Height + 6), layout.Detail, brush, DrawTextOptions.Clip);
            }
        }
        finally { rt.PopAxisAlignedClip(); }
    }
}

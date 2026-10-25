using System.Runtime.InteropServices;
using Agwinterm.Core;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

internal sealed partial class NativePicker
{
    private bool StyledRows => Request.Spec.Items.Any(i => i.Color is not null || i.Shape is not null);
    private static string RowText(PickItem item) => item.Label +
        (string.IsNullOrEmpty(item.Subtitle) ? "" : " — " + item.Subtitle) +
        (string.IsNullOrEmpty(item.Status) ? "" : " — " + item.Status);
    [StructLayout(LayoutKind.Sequential)] private struct DrawItem
    {
        public uint Type, Control, Item, Action, State;
        public IntPtr Window, Dc;
        public RECT Bounds;
        public IntPtr Data;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MeasureItem
    { public uint Type, Control, Item, Width, Height; public IntPtr Data; }
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT rect, IntPtr brush);
    [DllImport("user32.dll")] private static extern uint GetSysColor(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawTextW(IntPtr dc, string text, int count, ref RECT rect, uint format);
    [DllImport("user32.dll")] private static extern bool DrawFocusRect(IntPtr dc, ref RECT rect);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);

    private void MeasureRow(IntPtr address)
    {
        var item = Marshal.PtrToStructure<MeasureItem>(address);
        if (item.Control != 102) return;
        item.Height = (uint)Scale(24); Marshal.StructureToPtr(item, address, false);
    }

    private void DrawRow(IntPtr address)
    {
        var draw = Marshal.PtrToStructure<DrawItem>(address);
        if (draw.Control != 102) return;
        bool selected = (draw.State & 1) != 0;
        var brush = CreateSolidBrush((int)GetSysColor(selected ? 13 : 5));
        try { FillRect(draw.Dc, ref draw.Bounds, brush); } finally { DeleteObject(brush); }
        if (draw.Item == uint.MaxValue || draw.Item >= _selection.Count) return;
        var color = GetSysColor(selected ? 14 : 8);
        SetBkMode(draw.Dc, 1); SetTextColor(draw.Dc, color);
        var rect = draw.Bounds; rect.left += Scale(4);
        string text;
        if (draw.Item < _selection.Matches.Count)
        {
            var item = Request.Spec.Items[_selection.Matches[(int)draw.Item]];
            text = RowText(item);
            if (item.Color is not null || item.Shape is not null)
            {
                var marker = rect; marker.right = marker.left + Scale(20);
                if (item.Color is { } hex)
                {
                    uint rgb = Convert.ToUInt32(hex[1..], 16);
                    SetTextColor(draw.Dc, ((rgb & 255) << 16) | (rgb & 0xff00) | (rgb >> 16));
                }
                string glyph = item.Shape switch { "square" => "■", "diamond" => "◆", _ => "●" };
                DrawTextW(draw.Dc, glyph, glyph.Length, ref marker, 0x824); // single line, vcenter, no prefix
                SetTextColor(draw.Dc, color); rect.left += Scale(22);
            }
        }
        else text = "Use \"" + _selection.Query.Trim() + "\"";
        DrawTextW(draw.Dc, text, text.Length, ref rect, 0x8824); // end ellipsis
        if ((draw.State & 0x10) != 0) DrawFocusRect(draw.Dc, ref draw.Bounds);
    }
}

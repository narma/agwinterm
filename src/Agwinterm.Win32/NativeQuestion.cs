using System.Runtime.InteropServices;
using System.Text;
using Agwinterm.Core;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

/// <summary>Owned native paged form. All controls and saved values belong to the owner UI thread.</summary>
internal sealed class NativeQuestion : IDisposable
{
    private const string ClassName = "Agwinterm.NativeQuestion";
    private const uint NcDestroy = 0x82, GetDlgCode = 0x87;
    private static readonly WndProc RootProc = WindowProc;
    private static readonly Dictionary<IntPtr, NativeQuestion> Roots = new();
    private static NativeQuestion? _creating;
    private static bool _registered;
    private readonly IntPtr _owner;
    private readonly Action<AskOutcome> _finish;
    private readonly Action _activated;
    private readonly Action<string> _diagnostic;
    private readonly WndProc _childProc;
    private readonly Dictionary<IntPtr, IntPtr> _original = new();
    private readonly Dictionary<string, AskValue> _values = new(StringComparer.Ordinal);
    private readonly List<IntPtr> _buttons = new();
    private IntPtr _hwnd, _font, _prompt, _label, _field, _otherLabel, _other, _back, _next, _cancel;
    private int _dpi = 96, _page;
    private bool _ended, _disposing, _building;
    public PendingAsk Request { get; }
    private AskField? Field => Request.Spec.Fields.Count == 0 ? null : Request.Spec.Fields[_page];

    [DllImport("user32.dll")] private static extern IntPtr GetNextDlgTabItem(IntPtr dialog, IntPtr control, bool previous);
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr hwnd, bool enabled);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeout, uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitch, string family);

    public NativeQuestion(IntPtr owner, PendingAsk request, Action<AskOutcome> finish, Action activated, Action<string> diagnostic)
    { _owner = owner; Request = request; _finish = finish; _activated = activated; _diagnostic = diagnostic; _childProc = ChildProc; }

    public void Show(bool follow)
    {
        if (!_registered)
        {
            var cls = new WNDCLASSEXW { cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(), lpfnWndProc = RootProc,
                hInstance = GetModuleHandleW(null), hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW), hbrBackground = (IntPtr)6, lpszClassName = ClassName };
            if (RegisterClassExW(ref cls) == 0) throw new InvalidOperationException("question class registration failed");
            _registered = true;
        }
        _dpi = (int)GetDpiForWindow(_owner); if (_dpi <= 0) _dpi = 96;
        GetWindowRect(_owner, out var ownerRect);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(MonitorFromRect(ref ownerRect, MONITOR_DEFAULTTONEAREST), ref info)) throw new InvalidOperationException("question monitor unavailable");
        int width = Math.Min(Scale(640), info.rcWork.right - info.rcWork.left), height = Math.Min(Scale(580), info.rcWork.bottom - info.rcWork.top);
        int x = Math.Clamp(ownerRect.left + (ownerRect.right - ownerRect.left - width) / 2, info.rcWork.left, info.rcWork.right - width);
        int y = Math.Clamp(ownerRect.top + Scale(40), info.rcWork.top, info.rcWork.bottom - height);
        _creating = this;
        try { _hwnd = CreateWindowExW(0x10000, ClassName, Request.Spec.Title, WS_CAPTION | WS_SYSMENU | WS_POPUP,
            x, y, width, height, _owner, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero); }
        finally { _creating = null; }
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("question window creation failed");
        _prompt = Child("STATIC", Request.Spec.Prompt, 0x80, 100, false);
        _label = Child("STATIC", "", 0x80, 101, false);
        _otherLabel = Child("STATIC", "Other text", 0x80, 202, false);
        _back = Child("BUTTON", "Back", WS_TABSTOP, 3);
        _next = Child("BUTTON", "Next", WS_TABSTOP, 4);
        for (int i = 0; i < Request.Spec.Buttons.Count; i++)
        {
            var b = Request.Spec.Buttons[i];
            string label = b.Label.Replace("&", "&&") + (b.Hotkey is null ? "" : " (Alt+" + b.Hotkey + ")");
            _buttons.Add(Child("BUTTON", label, WS_TABSTOP | (i == 0 ? 1u : 0u), 1000 + i));
        }
        _cancel = Child("BUTTON", "Cancel", WS_TABSTOP, 2);
        UpdateFont(); BuildPage();
        if (_ended) throw new InvalidOperationException("question closed during creation");
        ShowWindow(_hwnd, 4);
        if (follow) SetForegroundWindow(_hwnd);
        FocusIfForeground();
    }

    private IntPtr Child(string cls, string text, uint style, int id, bool input = true)
    {
        var h = CreateWindowExW(0, cls, text, WS_CHILD | WS_VISIBLE | style, 0, 0, 1, 1, _hwnd, (IntPtr)id, GetModuleHandleW(null), IntPtr.Zero);
        if (h == IntPtr.Zero) throw new InvalidOperationException("question control creation failed");
        if (input)
        {
            _original[h] = SetWindowLongPtrW(h, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_childProc));
            if (_original[h] == IntPtr.Zero) throw new InvalidOperationException("question control subclass failed");
        }
        if (_font != IntPtr.Zero) SendMessageW(h, WM_SETFONT, _font, (IntPtr)1);
        return h;
    }
    private int Scale(int n) => Math.Max(1, (int)Math.Round(n * _dpi / 96.0));
    private void UpdateFont()
    {
        var font = CreateFontW(-Scale(14), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        if (font == IntPtr.Zero) throw new InvalidOperationException("question font creation failed");
        foreach (var h in _original.Keys.Concat(new[] { _prompt, _label, _otherLabel }))
            if (h != IntPtr.Zero) SendMessageW(h, WM_SETFONT, font, (IntPtr)1);
        if (_font != IntPtr.Zero) DeleteObject(_font); _font = font;
    }
    private void BuildPage()
    {
        _building = true;
        try
        {
            if (_field != IntPtr.Zero) { DestroyWindow(_field); _field = IntPtr.Zero; }
            if (_other != IntPtr.Zero) { DestroyWindow(_other); _other = IntPtr.Zero; }
            var f = Field;
            SetWindowTextW(_label, f is null ? "" : $"{_page + 1}/{Request.Spec.Fields.Count}  {f.Label}");
            EnableWindow(_back, _page > 0); EnableWindow(_next, _page + 1 < Request.Spec.Fields.Count);
            ShowWindow(_otherLabel, f?.AllowOther == true ? 5 : 0);
            if (f is not null)
            {
                var value = _values.GetValueOrDefault(f.Id) ?? AskValue.Empty(f);
                if (f.Type is "text" or "multiline")
                {
                    uint style = WS_TABSTOP | 0x800000 | (f.Type == "multiline" ? 0x1004u | WS_VSCROLL : ES_AUTOHSCROLL);
                    _field = Child("EDIT", value.Text, style, 200);
                    SendMessageW(_field, 0xc5, (IntPtr)(f.Type == "multiline" ? AskSpec.MaxMultiline : AskSpec.MaxText), IntPtr.Zero);
                }
                else
                {
                    _field = Child("LISTBOX", f.Label, WS_TABSTOP | WS_VSCROLL | 0x800101 | (f.Type == "multi" ? 8u : 0u), 200);
                    for (int i = 0; i < f.Choices.Count; i++)
                    {
                        if (SendMessageW(_field, 0x180, IntPtr.Zero, f.Choices[i].Label).ToInt64() < 0) throw new InvalidOperationException("question list allocation failed");
                        if (value.Selected.Contains(f.Choices[i].Id))
                            if (f.Type == "multi") SendMessageW(_field, 0x185, (IntPtr)1, (IntPtr)i);
                            else SendMessageW(_field, 0x186, (IntPtr)i, IntPtr.Zero);
                    }
                }
                if (f.AllowOther)
                {
                    _other = Child("EDIT", value.Other ?? "", WS_TABSTOP | ES_AUTOHSCROLL | 0x800000, 203);
                    SendMessageW(_other, 0xc5, (IntPtr)AskSpec.MaxText, IntPtr.Zero);
                }
                // Native edit accessibility derives its name from the preceding static label.
                SetWindowPos(_field, _label, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                SetWindowPos(_otherLabel, _field, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                if (_other != IntPtr.Zero) SetWindowPos(_other, _otherLabel, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            Layout();
        }
        finally { _building = false; }
    }
    private static string Read(IntPtr hwnd, int max)
    { var text = new StringBuilder(max + 1); GetWindowTextW(hwnd, text, text.Capacity); return text.ToString(); }
    private void SavePage()
    {
        if (Field is not { } f || _field == IntPtr.Zero) return;
        if (f.Type is "text" or "multiline") _values[f.Id] = new(Read(_field, AskSpec.MaxMultiline), [], null);
        else
        {
            var ids = new List<string>(); int selected = (int)SendMessageW(_field, 0x188, IntPtr.Zero, IntPtr.Zero);
            for (int i = 0; i < f.Choices.Count; i++)
                if (f.Type == "multi" ? SendMessageW(_field, 0x187, (IntPtr)i, IntPtr.Zero).ToInt64() > 0 : selected == i) ids.Add(f.Choices[i].Id);
            _values[f.Id] = new("", ids, f.AllowOther ? Read(_other, AskSpec.MaxText) : null);
        }
    }
    private void Page(int delta)
    { SavePage(); _page = Math.Clamp(_page + delta, 0, Math.Max(0, Request.Spec.Fields.Count - 1)); BuildPage(); FocusIfForeground(); }
    private void Submit(int index)
    { SavePage(); End(AskOutcome.Answer(Request.Spec, Request.Spec.Buttons[index].Id, _values)); }
    private void Layout()
    {
        if (_cancel == IntPtr.Zero) return;
        GetClientRect(_hwnd, out var r);
        int pad = Scale(12), gap = Scale(6), full = Math.Max(1, r.right - 2 * pad);
        int columns = Math.Max(1, Math.Min(4, full / Scale(120))), rows = (_buttons.Count + columns - 1) / columns;
        int buttonHeight = Scale(30), bottom = r.bottom - pad - buttonHeight;
        Place(_back, pad, bottom, Math.Max(1, (full - 2 * gap) / 3), buttonHeight);
        Place(_next, pad + (full + gap) / 3, bottom, Math.Max(1, (full - 2 * gap) / 3), buttonHeight);
        Place(_cancel, pad + 2 * (full + gap) / 3, bottom, Math.Max(1, (full - 2 * gap) / 3), buttonHeight);
        int actions = bottom - rows * (buttonHeight + gap);
        for (int i = 0; i < _buttons.Count; i++) Place(_buttons[i], pad + i % columns * (full + gap) / columns,
            actions + i / columns * (buttonHeight + gap), Math.Max(1, (full - (columns - 1) * gap) / columns), buttonHeight);
        int promptHeight = Request.Spec.Prompt.Length == 0 ? 0 : Math.Min(Scale(60), Math.Max(Scale(20), actions / 5));
        Place(_prompt, pad, pad, full, Math.Max(1, promptHeight));
        int labelTop = pad + promptHeight + gap, fieldTop = labelTop + Scale(40);
        Place(_label, pad, labelTop, full, Scale(36));
        int otherHeight = Field?.AllowOther == true ? Scale(58) : 0;
        int fieldHeight = Math.Max(Scale(24), actions - fieldTop - gap - otherHeight);
        if (Field?.Type == "text") fieldHeight = Math.Min(Scale(30), fieldHeight);
        Place(_field, pad, fieldTop, full, fieldHeight);
        Place(_otherLabel, pad, actions - Scale(58), full, Scale(22));
        Place(_other, pad, actions - Scale(34), full, Scale(28));
    }
    private static void Place(IntPtr h, int x, int y, int w, int height)
    { if (h != IntPtr.Zero) SetWindowPos(h, IntPtr.Zero, x, y, w, height, SWP_NOZORDER | SWP_NOACTIVATE); }
    public void FocusIfForeground()
    {
        if (_ended || _building) return;
        var front = GetForegroundWindow();
        if (front == _owner || front == _hwnd) SetFocus(_field != IntPtr.Zero ? _field : _buttons.FirstOrDefault());
    }
    public void Forward(uint message, IntPtr key, IntPtr detail)
    {
        if (_ended) return;
        var focus = GetFocus(); var target = _original.ContainsKey(focus) ? focus : _field != IntPtr.Zero ? _field : _buttons.FirstOrDefault();
        if (target != IntPtr.Zero) SendMessageW(target, message, key, detail);
    }
    private void End(AskOutcome outcome)
    { if (_ended) return; _ended = true; try { _finish(outcome); } finally { Dispose(); } }
    private void Command(int id)
    {
        if (id == 2) End(new("cancelled", "cancel"));
        else if (id == 3) Page(-1);
        else if (id == 4) Page(1);
        else if (id >= 1000 && id < 1000 + _buttons.Count) Submit(id - 1000);
    }
    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        if (!Roots.TryGetValue(hwnd, out var form))
        { form = _creating; if (form is null) return DefWindowProcW(hwnd, msg, w, l); form._hwnd = hwnd; Roots[hwnd] = form; }
        try
        {
            switch (msg)
            {
                case WM_CLOSE: form.End(new("cancelled", "closed")); return IntPtr.Zero;
                case WM_COMMAND:
                    if (((long)w >> 16 & 0xffff) == 0) form.Command((int)((long)w & 0xffff)); return IntPtr.Zero;
                case WM_SIZE: form.Layout(); break;
                case WM_ACTIVATE: if (((long)w & 0xffff) != 0) { form._activated(); form.FocusIfForeground(); } break;
                case WM_SETFOCUS: form.FocusIfForeground(); return IntPtr.Zero;
                case WM_DPICHANGED:
                    form._dpi = (int)((long)w & 0xffff); var rect = Marshal.PtrToStructure<RECT>(l);
                    SetWindowPos(hwnd, IntPtr.Zero, rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top, SWP_NOZORDER | SWP_NOACTIVATE);
                    form.UpdateFont(); form.Layout(); break;
                case NcDestroy:
                    Roots.Remove(hwnd); form._hwnd = IntPtr.Zero;
                    if (form._font != IntPtr.Zero) { DeleteObject(form._font); form._font = IntPtr.Zero; }
                    form.End(new("cancelled", "closed")); break;
            }
            return DefWindowProcW(hwnd, msg, w, l);
        }
        catch (Exception ex) { form.Fail(msg, ex); return IntPtr.Zero; }
    }
    private IntPtr ChildProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        if (!_original.TryGetValue(hwnd, out var original)) return DefWindowProcW(hwnd, msg, w, l);
        try
        {
            if (msg == GetDlgCode) return CallWindowProcW(original, hwnd, msg, w, l) | (IntPtr)4;
            if (msg == 0x106 /* WM_SYSCHAR */)
            {
                string key = ((char)(long)w).ToString();
                for (int i = 0; i < Request.Spec.Buttons.Count; i++)
                    if (string.Equals(Request.Spec.Buttons[i].Hotkey, key, StringComparison.OrdinalIgnoreCase)) { Submit(i); return IntPtr.Zero; }
            }
            if (msg == WM_KEYDOWN)
            {
                int key = (int)w;
                if (key == VK_ESCAPE) { End(new("cancelled", "escape")); return IntPtr.Zero; }
                if (key == VK_TAB) { SetFocus(GetNextDlgTabItem(_hwnd, hwnd, (GetKeyState(VK_SHIFT) & 0x8000) != 0)); return IntPtr.Zero; }
                if (key == VK_RETURN && !(hwnd == _field && Field?.Type == "multiline"))
                {
                    if (hwnd == _cancel) Command(2); else if (hwnd == _back) Command(3); else if (hwnd == _next) Command(4);
                    else { int button = _buttons.IndexOf(hwnd); Submit(button < 0 ? 0 : button); }
                    return IntPtr.Zero;
                }
                if ((hwnd == _other || (hwnd == _field && Field?.Type is "text" or "multiline")) && key == 65 && (GetKeyState(VK_CONTROL) & 0x8000) != 0)
                { SendMessageW(hwnd, EM_SETSEL, IntPtr.Zero, (IntPtr)(-1)); return IntPtr.Zero; }
            }
            if (msg == WM_CHAR && ((int)w is VK_ESCAPE or VK_TAB || ((int)w == VK_RETURN && !(hwnd == _field && Field?.Type == "multiline")))) return IntPtr.Zero;
            if (msg == NcDestroy) _original.Remove(hwnd);
            return CallWindowProcW(original, hwnd, msg, w, l);
        }
        catch (Exception ex) { Fail(msg, ex); return IntPtr.Zero; }
    }
    private void Fail(uint msg, Exception ex)
    { _diagnostic($"question wndproc ex msg=0x{msg:X}: {ex.GetType().Name} {ex.Message}"); try { End(new("cancelled", "closed")); } catch (Exception e) { _diagnostic(e.Message); } }
    public void Dispose()
    {
        if (_disposing) return; _disposing = true;
        bool restore = _hwnd != IntPtr.Zero && GetForegroundWindow() == _hwnd;
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        _original.Clear();
        if (_font != IntPtr.Zero) { DeleteObject(_font); _font = IntPtr.Zero; }
        if (restore && IsWindow(_owner) && GetForegroundWindow() == _owner) SetFocus(_owner);
        GC.KeepAlive(_childProc);
    }
}

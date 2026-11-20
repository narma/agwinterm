using System.Runtime.InteropServices;
using Agwinterm.Pty;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

internal partial class Program
{
    private const string LogWindowClass = "Agwinterm.ActivityLog";
    private static readonly WndProc LogProcedure = ActivityWindowProc;
    private static bool _logClassRegistered;
    private IntPtr _logWindow, _logList, _logDetail, _logCopy;
    private IReadOnlyList<ActivityEntry> _logEntries = [];
    private (DateTime Modified, long Length) _logVersion;

    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern bool IsDialogMessageW(IntPtr dialog, ref MSG message);

    private void ToggleActivityLog()
    {
        if (_logWindow != 0) { DestroyWindow(_logWindow); return; }
        if (!_logClassRegistered)
        {
            var cls = new WNDCLASSEXW { cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(), lpfnWndProc = LogProcedure,
                lpszClassName = LogWindowClass, hInstance = GetModuleHandleW(null), hCursor = LoadCursorW(0, IDC_ARROW),
                hbrBackground = (IntPtr)16 /* COLOR_BTNFACE + 1 */ };
            if (RegisterClassExW(ref cls) == 0) { ShowErrorToast("Could not open activity log"); return; }
            _logClassRegistered = true;
        }
        GetWindowRect(_hwnd, out var bounds);
        int width = Math.Min(ToDevice(820), bounds.right - bounds.left);
        int height = Math.Min(ToDevice(500), bounds.bottom - bounds.top);
        _logWindow = CreateWindowExW(0, LogWindowClass, "Agwinterm activity log",
            WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN, bounds.left + (bounds.right - bounds.left - width) / 2,
            bounds.top + (bounds.bottom - bounds.top - height) / 2, width, height, _hwnd, 0, GetModuleHandleW(null), 0);
        if (_logWindow == 0) { ShowErrorToast("Could not open activity log"); return; }

        IntPtr Child(string cls, string name, uint style, int id)
        {
            var control = CreateWindowExW(0, cls, name, WS_CHILD | WS_VISIBLE | WS_TABSTOP | style,
                0, 0, 1, 1, _logWindow, (IntPtr)id, GetModuleHandleW(null), 0);
            SendMessageW(control, WM_SETFONT, GetStockObject(17), (IntPtr)1);
            return control;
        }
        _logList = Child("LISTBOX", "", WS_VSCROLL | 1 /* LBS_NOTIFY */ | 0x100 /* LBS_NOINTEGRALHEIGHT */, 101);
        _logDetail = Child("EDIT", "", WS_VSCROLL | 4 /* ES_MULTILINE */ | 0x800 /* ES_READONLY */, 102);
        _logCopy = Child("BUTTON", "Copy entry", 0, 103);
        if (_logList == 0 || _logDetail == 0 || _logCopy == 0)
        {
            DestroyWindow(_logWindow);
            ShowErrorToast("Could not create activity log controls");
            return;
        }
        LayoutActivityLog();
        RefreshActivityLog(force: true);
        SetTimer(_logWindow, 1, 1000, 0);
        ShowWindow(_logWindow, 5);
        SetFocus(_logList);
    }

    private static IntPtr ActivityWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        if (_registry.TryGetValue(GetWindow(hwnd, 4 /* GW_OWNER */), out var owner))
        {
            switch (msg)
            {
                case WM_SIZE: owner.LayoutActivityLog(); break;
                case WM_TIMER: owner.RefreshActivityLog(force: false); return 0;
                case WM_COMMAND:
                    if (l == owner._logList && HiWord(w) == 1) owner.ShowSelectedActivity();
                    if (l == owner._logCopy && HiWord(w) == 0 && owner.SelectedActivity() is { } entry)
                        owner.ClipboardSet(entry.FullText);
                    if (LoWord(w) == 2 && l == 0) DestroyWindow(hwnd);
                    return 0;
                case WM_DESTROY:
                    owner._logWindow = owner._logList = owner._logDetail = owner._logCopy = 0;
                    owner._logEntries = [];
                    break;
            }
        }
        return DefWindowProcW(hwnd, msg, w, l);
    }

    private void LayoutActivityLog()
    {
        if (_logCopy == 0) return;
        GetClientRect(_logWindow, out var rect);
        int pad = ToDevice(8), button = ToDevice(30), width = Math.Max(1, rect.right - 2 * pad);
        int available = Math.Max(2, rect.bottom - 4 * pad - button), list = available * 3 / 5;
        void Place(IntPtr hwnd, int x, int y, int w, int h) =>
            SetWindowPos(hwnd, 0, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
        Place(_logList, pad, pad, width, list);
        Place(_logDetail, pad, 2 * pad + list, width, available - list);
        Place(_logCopy, pad, 3 * pad + available, ToDevice(110), button);
    }

    private ActivityEntry? SelectedActivity()
    {
        int index = (int)SendMessageW(_logList, 0x188 /* LB_GETCURSEL */, 0, 0);
        return (uint)index < _logEntries.Count ? _logEntries[index] : null;
    }

    private void ShowSelectedActivity() =>
        SetWindowTextW(_logDetail, SelectedActivity()?.FullText ?? "No activity recorded");

    private void RefreshActivityLog(bool force)
    {
        if (_logList == 0) return;
        var version = Journal.Value.Version;
        if (!force && _logVersion == version) return;
        _logVersion = version;
        var selected = SelectedActivity();
        var entries = Journal.Value.Read();
        int index = 0;
        SendMessageW(_logList, 0x184 /* LB_RESETCONTENT */, 0, 0);
        for (int i = 0; i < entries.Count; i++)
        {
            SendMessageW(_logList, 0x180 /* LB_ADDSTRING */, 0, entries[i].Heading);
            if (entries[i] == selected) index = i;
        }
        _logEntries = entries;
        SendMessageW(_logList, 0x186 /* LB_SETCURSEL */, (IntPtr)index, 0);
        if (force || SelectedActivity() != selected) ShowSelectedActivity();
    }
}

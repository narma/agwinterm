using System.Runtime.InteropServices;

namespace Agwinterm.Win32;

internal static class AboutDialog
{
    internal static void Show(IntPtr owner, string content)
    {
        const string project = "https://github.com/narma/agwinterm";
        const string engine = "https://lib.ghostty.org/vt/";
        TaskDialogCallback callback = (window, notification, wParam, lParam, data) =>
        {
            if (notification == 3) // TDN_HYPERLINK_CLICKED
            {
                string? url = Marshal.PtrToStringUni(lParam);
                if (url is project or engine)
                    Win32.ShellExecuteW(window, "open", url, null, null, Win32.SW_SHOW);
            }
            return 0;
        };
        var config = new TaskDialogConfig
        {
            Size = (uint)Marshal.SizeOf<TaskDialogConfig>(),
            Parent = owner,
            Flags = 0x0001 | 0x0008 | 0x1000, // hyperlinks, cancellation, center on owner
            CommonButtons = 1, // OK
            WindowTitle = "About agwinterm",
            Content = content.Replace(project, $"<a href=\"{project}\">{project}</a>")
                .Replace(engine, $"<a href=\"{engine}\">{engine}</a>"),
            Callback = callback,
            Width = 340,
        };
        int result = TaskDialogIndirect(ref config, out _, out _, out _);
        GC.KeepAlive(callback);
        if (result < 0)
            Win32.MessageBoxW(owner, content, "About agwinterm", Win32.MB_OK | Win32.MB_ICONINFORMATION);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int TaskDialogCallback(IntPtr window, uint notification, UIntPtr wParam, IntPtr lParam, IntPtr data);

    // commctrl.h declares TASKDIALOGCONFIG with one-byte packing.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    private struct TaskDialogConfig
    {
        public uint Size;
        public IntPtr Parent, Instance;
        public uint Flags, CommonButtons;
        public string? WindowTitle;
        public IntPtr MainIcon;
        public string? MainInstruction, Content;
        public uint ButtonCount;
        public IntPtr Buttons;
        public int DefaultButton;
        public uint RadioButtonCount;
        public IntPtr RadioButtons;
        public int DefaultRadioButton;
        public string? VerificationText, ExpandedInformation, ExpandedControlText, CollapsedControlText;
        public IntPtr FooterIcon;
        public string? Footer;
        public TaskDialogCallback? Callback;
        public IntPtr CallbackData;
        public uint Width;
    }

    [DllImport("comctl32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int TaskDialogIndirect(ref TaskDialogConfig config,
        out int button, out int radioButton, out int verificationChecked);
}
